#pragma once

#include <Windows.h>

#include <cstddef>
#include <functional>
#include <memory>
#include <string>
#include <vector>

// Reads LF-delimited UTF-8 messages from an overlapped named-pipe handle.
//
// At most one read is outstanding. A read has exactly one completion sink:
// ReadFile returning TRUE (the bytes were already buffered) or a pending read
// signalling its completion event. Both funnel through Deliver(), so a
// completion's bytes are delivered exactly once. Windows signals the completion
// event for synchronous completions as well, which is why the synchronous path
// must never wait on, or reap, the same OVERLAPPED a second time.
class NativePipeReader
{
public:
    using MessageSink = std::function<void(std::string)>;

    NativePipeReader(MessageSink sink,
        std::size_t maximumMessage = 4 * 1024 * 1024,
        std::size_t readBufferSize = 64 * 1024);
    ~NativePipeReader();

    NativePipeReader(const NativePipeReader&) = delete;
    NativePipeReader& operator=(const NativePipeReader&) = delete;

    // Allocates the completion event and the read buffer. When this fails the
    // connection must not be read; LastError() carries the Win32 code.
    bool Initialize();

    HANDLE CompletionEvent() const { return completionEvent; }
    bool HasPendingRead() const { return pending; }
    DWORD LastError() const { return lastError; }

    enum class StartResult
    {
        Pending,   // ReadFile returned ERROR_IO_PENDING; wait on CompletionEvent().
        Completed, // ReadFile returned TRUE; call Finish() without waiting.
        Failed     // The connection must be retired; see LastError().
    };

    // Starts the single outstanding read. Never call while HasPendingRead().
    StartResult Start(HANDLE pipe);

    enum class PumpResult
    {
        Data,      // The read completed and its bytes were delivered to the sink.
        Woken,     // The wake handle was signalled; no completion was consumed.
        TimedOut,  // A finite timeout expired; the read is still outstanding.
        Failed     // The connection must be retired; see LastError().
    };

    // Completes the read started by Start() and hands its bytes to the sink
    // exactly once. For a synchronous completion this delivers immediately and
    // never waits.
    PumpResult Finish(HANDLE pipe, HANDLE wake, DWORD timeoutMilliseconds = INFINITE);

    // Starts a read when idle, or resumes the outstanding read after Woken or
    // TimedOut. The product worker loop reads through this entry point.
    PumpResult Pump(HANDLE pipe, HANDLE wake, DWORD timeoutMilliseconds = INFINITE);

    // Cancels the outstanding read and waits until it has left the I/O manager,
    // so the OVERLAPPED and buffer can be released. Returns false when the read
    // had to be abandoned; its memory is then leaked on purpose because the
    // kernel may still write into it.
    bool CancelPending(HANDLE pipe, DWORD timeoutMilliseconds = 2000);

    bool Abandoned() const { return abandoned; }

private:
    struct ReadState
    {
        OVERLAPPED operation{};
        std::vector<char> buffer;
    };

    bool Deliver(HANDLE pipe);
    bool Append(const char* data, std::size_t length);
    void Abandon();

    MessageSink sink;
    std::size_t maximumMessage;
    std::size_t readBufferSize;
    std::unique_ptr<ReadState> state;
    std::string partial;
    HANDLE completionEvent{};
    bool initialized{};
    bool pending{};
    bool synchronous{};
    DWORD synchronousBytes{};
    DWORD lastError{};
    bool abandoned{};
};
