#include "NativePipeReader.h"

#include <utility>

NativePipeReader::NativePipeReader(MessageSink sink,
    std::size_t maximumMessage,
    std::size_t readBufferSize)
    : sink(std::move(sink)),
      maximumMessage(maximumMessage),
      readBufferSize(readBufferSize ? readBufferSize : 1)
{
}

NativePipeReader::~NativePipeReader()
{
    // A pending read cannot be cancelled without the pipe handle, so abandon its
    // memory instead of freeing something the kernel may still write into. The
    // product always calls CancelPending() before tearing a connection down.
    if (pending)
    {
        Abandon();
    }
    if (completionEvent)
    {
        CloseHandle(completionEvent);
    }
}

bool NativePipeReader::Initialize()
{
    if (initialized)
    {
        return true;
    }
    if (!completionEvent)
    {
        completionEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!completionEvent)
        {
            lastError = GetLastError();
            return false;
        }
    }
    try
    {
        auto created = std::make_unique<ReadState>();
        created->buffer.resize(readBufferSize);
        state = std::move(created);
    }
    catch (...)
    {
        lastError = ERROR_NOT_ENOUGH_MEMORY;
        return false;
    }
    initialized = true;
    return true;
}

NativePipeReader::StartResult NativePipeReader::Start(HANDLE pipe)
{
    if (!initialized || !state || !pipe || pipe == INVALID_HANDLE_VALUE)
    {
        lastError = ERROR_INVALID_HANDLE;
        return StartResult::Failed;
    }
    if (pending)
    {
        // Only one read may be outstanding, so there is nothing to start.
        return StartResult::Pending;
    }
    if (synchronous)
    {
        // The previous synchronous completion has not been delivered yet.
        lastError = ERROR_INVALID_STATE;
        return StartResult::Failed;
    }
    ResetEvent(completionEvent);
    ZeroMemory(&state->operation, sizeof(state->operation));
    state->operation.hEvent = completionEvent;
    DWORD received{};
    if (ReadFile(pipe, state->buffer.data(),
            static_cast<DWORD>(state->buffer.size()), &received, &state->operation))
    {
        synchronous = true;
        synchronousBytes = received;
        return StartResult::Completed;
    }
    const DWORD error = GetLastError();
    if (error == ERROR_IO_PENDING)
    {
        pending = true;
        return StartResult::Pending;
    }
    lastError = error;
    return StartResult::Failed;
}

NativePipeReader::PumpResult NativePipeReader::Finish(HANDLE pipe, HANDLE wake,
    DWORD timeoutMilliseconds)
{
    if (!pending)
    {
        if (!synchronous)
        {
            lastError = ERROR_INVALID_STATE;
            return PumpResult::Failed;
        }
        return Deliver(pipe) ? PumpResult::Data : PumpResult::Failed;
    }
    HANDLE handles[2]{ completionEvent, wake };
    const DWORD count = wake ? 2 : 1;
    const DWORD waited = WaitForMultipleObjects(count, handles, FALSE, timeoutMilliseconds);
    if (waited == WAIT_OBJECT_0)
    {
        return Deliver(pipe) ? PumpResult::Data : PumpResult::Failed;
    }
    if (waited == WAIT_OBJECT_0 + 1 && wake)
    {
        return PumpResult::Woken;
    }
    if (waited == WAIT_TIMEOUT)
    {
        return PumpResult::TimedOut;
    }
    lastError = waited == WAIT_FAILED ? GetLastError() : ERROR_INVALID_HANDLE;
    return PumpResult::Failed;
}

NativePipeReader::PumpResult NativePipeReader::Pump(HANDLE pipe, HANDLE wake,
    DWORD timeoutMilliseconds)
{
    if (pending)
    {
        // A wake lets the caller send commands; a timeout lets it check its
        // deadline. Neither completes the outstanding read. Resume that read
        // instead of disconnecting or submitting another OVERLAPPED operation.
        return Finish(pipe, wake, timeoutMilliseconds);
    }
    const StartResult started = Start(pipe);
    if (started == StartResult::Failed)
    {
        return PumpResult::Failed;
    }
    return Finish(pipe, wake, timeoutMilliseconds);
}

bool NativePipeReader::Deliver(HANDLE pipe)
{
    DWORD received{};
    if (pending)
    {
        if (!GetOverlappedResult(pipe, &state->operation, &received, FALSE))
        {
            const DWORD error = GetLastError();
            if (error != ERROR_IO_INCOMPLETE)
            {
                // The read is finished with an error: reap it and retire the
                // connection. ERROR_IO_INCOMPLETE keeps it outstanding.
                pending = false;
                lastError = error;
            }
            return false;
        }
        pending = false;
    }
    else if (synchronous)
    {
        received = synchronousBytes;
        synchronous = false;
    }
    else
    {
        lastError = ERROR_INVALID_STATE;
        return false;
    }
    if (!received)
    {
        // A zero-byte read means the peer closed its end of the pipe.
        lastError = ERROR_BROKEN_PIPE;
        return false;
    }
    return Append(state->buffer.data(), received);
}

bool NativePipeReader::Append(const char* data, std::size_t length)
{
    partial.append(data, length);
    for (;;)
    {
        const std::size_t line = partial.find('\n');
        if (line == std::string::npos)
        {
            break;
        }
        if (line > maximumMessage)
        {
            lastError = ERROR_MORE_DATA;
            return false;
        }
        std::string message = partial.substr(0, line);
        partial.erase(0, line + 1);
        sink(std::move(message));
    }
    if (partial.size() > maximumMessage)
    {
        lastError = ERROR_MORE_DATA;
        return false;
    }
    return true;
}

bool NativePipeReader::CancelPending(HANDLE pipe, DWORD timeoutMilliseconds)
{
    if (!pending)
    {
        return true;
    }
    const DWORD cancel = CancelIoEx(pipe, &state->operation) ? ERROR_SUCCESS : GetLastError();
    if (cancel != ERROR_SUCCESS && cancel != ERROR_NOT_FOUND)
    {
        // Recorded for diagnostics; the completion is still reaped below.
        lastError = cancel;
    }
    DWORD received{};
    const DWORD waited = WaitForSingleObject(completionEvent, timeoutMilliseconds);
    if (waited == WAIT_OBJECT_0)
    {
        pending = false;
        if (GetOverlappedResult(pipe, &state->operation, &received, FALSE))
        {
            return true;
        }
        const DWORD error = GetLastError();
        if (error == ERROR_OPERATION_ABORTED)
        {
            // The cancel did what it promised.
            lastError = error;
            return true;
        }
        lastError = error;
        return false;
    }
    if (waited == WAIT_TIMEOUT)
    {
        // The completion did not arrive. It may still be in flight, so its
        // memory must not be freed and the worker must not block forever.
        if (GetOverlappedResult(pipe, &state->operation, &received, FALSE))
        {
            pending = false;
            return true;
        }
        lastError = ERROR_TIMEOUT;
        Abandon();
        return false;
    }
    lastError = waited == WAIT_FAILED ? GetLastError() : ERROR_INVALID_HANDLE;
    Abandon();
    return false;
}

void NativePipeReader::Abandon()
{
    // Deliberately leak the OVERLAPPED, its buffer and the completion event: an
    // in-flight read may still write into them. This only happens when teardown
    // cannot prove the read left the I/O manager, and a leaked few hundred
    // kilobytes beats a use-after-free.
    pending = false;
    synchronous = false;
    abandoned = true;
    state.release();
    completionEvent = nullptr;
}
