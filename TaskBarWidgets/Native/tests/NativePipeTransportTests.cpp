// Regression tests for the overlapped named-pipe reader used by the media host.
//
// The removed product loop (NativeMediaHost.cpp Run(), fda626e) delivered a read
// twice: once when ReadFile returned TRUE and again when the completion event -
// which Windows also signals for synchronous completions - was reaped with
// GetOverlappedResult. These tests drive the real NativePipeReader against a
// real named pipe and prove both completion paths deliver each fragment once.
// LegacyConsumer reproduces the removed loop so the same fixture fails on the
// old code and passes on the fixed code.

#include "../TrafficMonitor/NativePipeReader.h"

#include <Windows.h>

#include <atomic>
#include <cstdio>
#include <memory>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace
{
    void Check(bool condition, const char* what)
    {
        if (!condition) throw std::runtime_error(what);
    }

    std::wstring UniquePipeName()
    {
        static std::atomic<unsigned> serial{ 0 };
        wchar_t name[160]{};
        swprintf_s(name, L"\\\\.\\pipe\\TaskBarWidgets.Test.%u.%llu.%u",
            GetCurrentProcessId(),
            static_cast<unsigned long long>(GetTickCount64()),
            ++serial);
        return name;
    }

    // One server end with a private name, sized so a test writer never blocks on
    // a reader that is intentionally not consuming data.
    class ServerPipe
    {
    public:
        explicit ServerPipe(unsigned bufferBytes = 256 * 1024)
        {
            name = UniquePipeName();
            handle = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1,
                bufferBytes, bufferBytes, 0, nullptr);
            if (handle == INVALID_HANDLE_VALUE)
                throw std::runtime_error("CreateNamedPipeW failed");
        }

        ~ServerPipe() { Disconnect(); }

        ServerPipe(const ServerPipe&) = delete;
        ServerPipe& operator=(const ServerPipe&) = delete;

        // Opens the client end exactly like the product does.
        HANDLE Connect()
        {
            HANDLE client = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                nullptr, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                nullptr);
            if (client == INVALID_HANDLE_VALUE)
                throw std::runtime_error("CreateFileW failed");
            if (!ConnectNamedPipe(handle, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED)
            {
                CloseHandle(client);
                throw std::runtime_error("ConnectNamedPipe failed");
            }
            return client;
        }

        void Write(const std::string& text)
        {
            size_t offset{};
            while (offset < text.size())
            {
                DWORD sent{};
                if (!WriteFile(handle, text.data() + offset,
                        static_cast<DWORD>(text.size() - offset), &sent, nullptr) ||
                    sent == 0)
                    throw std::runtime_error("server WriteFile failed");
                offset += sent;
            }
        }

        void Disconnect()
        {
            if (handle != INVALID_HANDLE_VALUE)
            {
                CloseHandle(handle);
                handle = INVALID_HANDLE_VALUE;
            }
        }

    private:
        std::wstring name;
        HANDLE handle{ INVALID_HANDLE_VALUE };
    };

    // Writes without blocking the pumping thread.
    struct AsyncWrite
    {
        std::thread worker;
        std::atomic<bool> finished{ false };
        std::string failure;

        AsyncWrite(ServerPipe& server, std::string data)
        {
            worker = std::thread([this, &server, data = std::move(data)]
            {
                try
                {
                    server.Write(data);
                    finished = true;
                }
                catch (const std::exception& error)
                {
                    failure = error.what();
                }
            });
        }

        ~AsyncWrite()
        {
            if (worker.joinable()) worker.join();
        }

        void Join()
        {
            if (worker.joinable()) worker.join();
            if (!finished)
                throw std::runtime_error(failure.empty() ? "background write failed" : failure.c_str());
        }
    };

    // Drips bytes one at a time so a single write can never span a read.
    struct DripWrite
    {
        std::thread worker;

        DripWrite(ServerPipe& server, std::string data)
        {
            worker = std::thread([&server, data = std::move(data)]
            {
                for (char ch : data) server.Write(std::string(1, ch));
            });
        }

        ~DripWrite()
        {
            if (worker.joinable()) worker.join();
        }
    };

    NativePipeReader::MessageSink Collect(std::vector<std::string>& messages)
    {
        return [&messages](std::string message) { messages.push_back(std::move(message)); };
    }

    // One production read. Fails the test when the connection ends instead.
    void PumpOnce(NativePipeReader& reader, HANDLE pipe, const char* what)
    {
        Check(reader.Pump(pipe, nullptr, 5000) == NativePipeReader::PumpResult::Data, what);
    }

    void PumpUntil(NativePipeReader& reader, HANDLE pipe,
        std::vector<std::string>& messages, size_t expected, const char* what)
    {
        const ULONGLONG deadline = GetTickCount64() + 15000;
        while (messages.size() < expected)
        {
            Check(GetTickCount64() < deadline, what);
            Check(reader.Pump(pipe, nullptr, 1000) != NativePipeReader::PumpResult::Failed, what);
            Check(messages.size() <= expected, "a read must never frame more messages than expected");
        }
    }

    void PumpUntilFailure(NativePipeReader& reader, HANDLE pipe, const char* what)
    {
        const ULONGLONG deadline = GetTickCount64() + 20000;
        while (GetTickCount64() < deadline)
        {
            if (reader.Pump(pipe, nullptr, 1000) == NativePipeReader::PumpResult::Failed) return;
        }
        throw std::runtime_error(what);
    }

    const std::string kFirstFragment = "{\"version\":1,\"ty";
    const std::string kSecondFragment = "pe\":\"initialize\",\"renderVersion\":2}\n";
    const std::vector<std::string> kInitialize{ "{\"version\":1,\"type\":\"initialize\",\"renderVersion\":2}" };

    // ---- A: synchronous completion ------------------------------------------

    void SyncCompletionDeliversFragmentsOnce()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");

        server.Write(kFirstFragment);
        Check(reader.Start(client) == NativePipeReader::StartResult::Completed,
            "a read with buffered bytes must complete synchronously");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data,
            "the synchronous completion must deliver");
        Check(messages.empty(), "a partial message must not be delivered");

        server.Write(kSecondFragment);
        Check(reader.Start(client) == NativePipeReader::StartResult::Completed,
            "the second read must also complete synchronously");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data,
            "the second synchronous completion must deliver");
        Check(messages == kInitialize,
            "synchronous fragments must be reassembled exactly once");
        CloseHandle(client);
    }

    // ---- B: pending completion ----------------------------------------------

    void PendingCompletionDeliversFragmentsOnce()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");

        Check(reader.Start(client) == NativePipeReader::StartResult::Pending,
            "an empty pipe must report a pending read");
        Check(reader.HasPendingRead(), "the pending read is tracked");
        server.Write(kFirstFragment);
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data,
            "the pending completion must deliver");
        Check(messages.empty(), "a partial message must not be delivered");

        Check(reader.Start(client) == NativePipeReader::StartResult::Pending,
            "the next read must pend again");
        server.Write(kSecondFragment);
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data,
            "the second pending completion must deliver");
        Check(messages == kInitialize,
            "pending fragments must be reassembled exactly once");
        CloseHandle(client);
    }

    // ---- C: alternating completion paths ------------------------------------

    void AlternatingCompletionsReuseState()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");

        server.Write("{\"n\":1}\n");
        Check(reader.Start(client) == NativePipeReader::StartResult::Completed, "sync read");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data, "sync delivery");

        Check(reader.Start(client) == NativePipeReader::StartResult::Pending, "pending read");
        server.Write("{\"n\":2}\n");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data, "pending delivery");

        server.Write("{\"n\":3}\n");
        Check(reader.Start(client) == NativePipeReader::StartResult::Completed, "third sync read");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data, "third delivery");

        Check(reader.Start(client) == NativePipeReader::StartResult::Pending, "fourth pending read");
        server.Write("{\"n\":4}\n");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data, "fourth delivery");

        const std::vector<std::string> expected{ "{\"n\":1}", "{\"n\":2}", "{\"n\":3}", "{\"n\":4}" };
        Check(messages == expected, "alternating completions must keep order and content");
        CloseHandle(client);
    }

    // ---- D: message boundaries ----------------------------------------------

    void MultipleMessagesInOneRead()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        server.Write("{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        PumpUntil(reader, client, messages, 3, "one read must frame three messages");
        const std::vector<std::string> expected{ "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" };
        Check(messages == expected, "every message in a joined read must be framed once");
        CloseHandle(client);
    }

    void TrailingLfArrivesSeparately()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        server.Write("{\"a\":1}");
        PumpOnce(reader, client, "the read without its LF must still complete");
        Check(messages.empty(), "a message without its LF must not be delivered");
        server.Write("\n");
        PumpOnce(reader, client, "the LF must complete the message");
        Check(messages == std::vector<std::string>{ "{\"a\":1}" }, "the LF must not leak into the message");
        CloseHandle(client);
    }

    void Utf8SplitInsideCodePoint()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        // {"text":"<CJK CJK CJK>"} written as UTF-8 bytes.
        const std::string message =
            "{\"text\":\"\xE4\xB8\xAD\xE6\x96\x87\xE5\xAD\x97\"}";
        const std::string framed = message + "\n";
        const size_t lead = message.find('\xE4');
        Check(lead != std::string::npos, "fixture has a multi-byte character");
        const size_t cut = lead + 2;  // split inside the first code point
        server.Write(framed.substr(0, cut));
        PumpOnce(reader, client, "the incomplete UTF-8 fragment must be consumed");
        Check(messages.empty(), "an incomplete UTF-8 sequence must not be delivered");
        server.Write(framed.substr(cut));
        PumpOnce(reader, client, "the rest of the UTF-8 message must complete it");
        Check(messages == std::vector<std::string>{ message }, "split UTF-8 bytes must reassemble exactly");
        CloseHandle(client);
    }

    void ByteAtATime()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        const std::string message = "{\"type\":\"frame\",\"revision\":7,\"trackId\":\"t-1\"}";
        {
            DripWrite drip(server, message + "\n");
        }
        PumpUntil(reader, client, messages, 1, "a one-byte-at-a-time write must still frame once");
        Check(messages == std::vector<std::string>{ message }, "byte-level fragmentation must not duplicate data");
        CloseHandle(client);
    }

    void LargeMessageAcrossReadBuffer()
    {
        ServerPipe server(2 * 1024 * 1024);
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 1024 * 1024, 1024);
        Check(reader.Initialize(), "reader initialised");
        const std::string body = "{\"data\":\"" + std::string(256 * 1024, 'x') + "\"}";
        {
            AsyncWrite write(server, body + "\n");
            PumpUntil(reader, client, messages, 1, "a message larger than the read buffer must complete");
            write.Join();
        }
        Check(messages.size() == 1 && messages[0] == body,
            "a message spanning many reads must be reassembled exactly");
        CloseHandle(client);
    }

    void MessageLimitHonoursBoundaries()
    {
        constexpr size_t limit = 256;
        {
            ServerPipe server;
            HANDLE client = server.Connect();
            std::vector<std::string> messages;
            NativePipeReader reader(Collect(messages), limit);
            Check(reader.Initialize(), "reader initialised");
            server.Write(std::string(limit, 'a') + "\n");
            PumpUntil(reader, client, messages, 1, "a message at the limit must be accepted");
            Check(messages.size() == 1 && messages[0].size() == limit, "the limit is inclusive");
            CloseHandle(client);
        }
        {
            ServerPipe server;
            HANDLE client = server.Connect();
            std::vector<std::string> messages;
            NativePipeReader reader(Collect(messages), limit);
            Check(reader.Initialize(), "reader initialised");
            server.Write(std::string(limit + 1, 'a') + "\n");
            PumpUntilFailure(reader, client, "a message over the limit must fail the connection");
            Check(reader.LastError() == ERROR_MORE_DATA, "the oversize failure must be reported");
            Check(messages.empty(), "an oversize message must not be delivered");
            CloseHandle(client);
        }
    }

    void ProductLimitAcceptsFourMegabyteMessage()
    {
        ServerPipe server(5 * 1024 * 1024);
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages));
        Check(reader.Initialize(), "reader initialised");
        const std::string body(4 * 1024 * 1024, 'm');
        {
            AsyncWrite write(server, body + "\n");
            PumpUntil(reader, client, messages, 1, "the product limit must accept exactly four megabytes");
            write.Join();
        }
        Check(messages.size() == 1 && messages[0] == body, "the four megabyte payload must be intact");
        CloseHandle(client);
    }

    void ProductLimitRejectsOversizedMessage()
    {
        ServerPipe server(5 * 1024 * 1024);
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages));
        Check(reader.Initialize(), "reader initialised");
        const std::string body(4 * 1024 * 1024 + 1, 'm');
        AsyncWrite write(server, body + "\n");
        PumpUntilFailure(reader, client, "one byte over the product limit must fail the connection");
        Check(reader.LastError() == ERROR_MORE_DATA, "the oversize failure must be reported");
        Check(messages.empty(), "an oversized product message must not be delivered");
        write.Join();
        CloseHandle(client);
    }

    // ---- E: lifecycle -------------------------------------------------------

    void DisconnectWhilePending()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        Check(reader.Start(client) == NativePipeReader::StartResult::Pending, "read pending");
        server.Disconnect();
        const ULONGLONG started = GetTickCount64();
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Failed,
            "a server disconnect must fail the read");
        Check(GetTickCount64() - started < 4000, "the disconnect must not hang");
        Check(reader.LastError() != 0, "the disconnect must report an error");
        Check(!reader.HasPendingRead(), "the failed read must be reaped");
        Check(messages.empty(), "a failed read must not deliver data");
        CloseHandle(client);
    }

    void CancelPendingReadCompletes()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        Check(reader.Start(client) == NativePipeReader::StartResult::Pending, "read pending");
        Check(reader.CancelPending(client, 5000), "cancelling the pending read must complete");
        Check(!reader.HasPendingRead(), "the cancelled read must be reaped");
        Check(!reader.Abandoned(), "a completed cancel must release its memory");
        Check(messages.empty(), "a cancelled read must not deliver data");
        Check(reader.CancelPending(client, 5000), "cancelling again is a no-op");
        CloseHandle(client);
    }

    void CancelRacingCompletionDoesNotDeadlock()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        Check(reader.Start(client) == NativePipeReader::StartResult::Pending, "read pending");
        // The completion races the cancel: either order must leave the reader
        // consistent and must not free memory an in-flight read can still touch.
        server.Write("{\"race\":1}\n");
        Check(reader.CancelPending(client, 5000), "a cancel racing a completion must complete");
        Check(!reader.HasPendingRead(), "the racing read must be reaped");
        Check(!reader.Abandoned(), "a racing cancel must not abandon its memory");
        Check(messages.empty(), "a cancelled read must not deliver data");
        CloseHandle(client);
    }

    void ReconnectDoesNotReuseStaleState()
    {
        ServerPipe first;
        HANDLE firstClient = first.Connect();
        std::vector<std::string> firstMessages;
        NativePipeReader firstReader(Collect(firstMessages), 4096);
        Check(firstReader.Initialize(), "first reader initialised");
        first.Write(kFirstFragment);
        PumpOnce(firstReader, firstClient, "the first connection consumes its fragment");
        Check(firstMessages.empty(), "the first connection must not frame a partial message");
        firstReader.CancelPending(firstClient);
        CloseHandle(firstClient);
        first.Disconnect();

        ServerPipe second;
        HANDLE secondClient = second.Connect();
        std::vector<std::string> secondMessages;
        NativePipeReader secondReader(Collect(secondMessages), 4096);
        Check(secondReader.Initialize(), "second reader initialised");
        second.Write(kInitialize[0] + "\n");
        PumpUntil(secondReader, secondClient, secondMessages, 1,
            "the rebuilt connection must frame its first message");
        Check(secondMessages == kInitialize,
            "the old connection's partial bytes must not leak into the new one");
        CloseHandle(secondClient);
    }

    void IdleReadDoesNotInheritStaleSignal()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");

        server.Write("{\"n\":1}\n");
        Check(reader.Start(client) == NativePipeReader::StartResult::Completed, "sync read");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data, "sync delivery");

        // Windows signalled the event for that synchronous completion. A fresh
        // read must reset it, otherwise the worker spins on a stale signal.
        Check(reader.Start(client) == NativePipeReader::StartResult::Pending, "idle read pending");
        Check(WaitForSingleObject(reader.CompletionEvent(), 200) == WAIT_TIMEOUT,
            "an idle read must not see the previous completion signal");
        server.Write("{\"n\":2}\n");
        Check(reader.Finish(client, nullptr, 5000) == NativePipeReader::PumpResult::Data, "idle read completes");
        Check(messages == std::vector<std::string>{ "{\"n\":1}", "{\"n\":2}" }, "the idle wake keeps order");
        CloseHandle(client);
    }

    void WakePreservesPendingRead()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        HANDLE wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        SetEvent(wake);
        Check(reader.Pump(client, wake, 5000) == NativePipeReader::PumpResult::Woken,
            "the wake handle must win the wait");
        Check(reader.HasPendingRead(), "a wake must not consume the pending read");
        Check(messages.empty(), "a wake must not deliver data");
        Check(WaitForSingleObject(wake, 0) == WAIT_TIMEOUT,
            "the product's auto-reset wake must be consumed by the wait");
        SetEvent(wake);
        Check(reader.Pump(client, wake, 5000) == NativePipeReader::PumpResult::Woken,
            "repeated outbound wakes must preserve the same pending read");
        server.Write("{\"n\":1}\n");
        Check(reader.Pump(client, wake, 5000) == NativePipeReader::PumpResult::Data,
            "the read must still complete after a wake");
        Check(messages == std::vector<std::string>{ "{\"n\":1}" }, "the woken read keeps its bytes");
        CloseHandle(wake);
        CloseHandle(client);
    }

    void PumpResumesAfterTimeout()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            Check(reader.Pump(client, nullptr, 0) == NativePipeReader::PumpResult::TimedOut,
                "an idle pump must resume the outstanding read after a timeout");
            Check(reader.HasPendingRead(), "timeout preserves the outstanding read");
            Check(messages.empty(), "timeout cannot deliver data");
        }
        server.Write("{\"n\":1}\n");
        Check(reader.Pump(client, nullptr, 5000) == NativePipeReader::PumpResult::Data,
            "a timed-out read must still complete through Pump");
        Check(messages == std::vector<std::string>{ "{\"n\":1}" },
            "the resumed read delivers exactly once");
        CloseHandle(client);
    }

    void InvalidCallsFailFast()
    {
        std::vector<std::string> messages;
        NativePipeReader reader(Collect(messages), 4096);
        Check(reader.Initialize(), "reader initialised");
        Check(reader.Start(nullptr) == NativePipeReader::StartResult::Failed,
            "a null pipe must fail instead of waiting");
        Check(reader.Finish(nullptr, nullptr, 0) == NativePipeReader::PumpResult::Failed,
            "finishing without a read must fail instead of waiting");
        Check(reader.CancelPending(nullptr, 0), "cancelling without a pending read is a no-op");
        Check(!reader.Abandoned(), "an unused reader is never abandoned");
    }

    void UnreapedCancelAbandonsInFlightMemory()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        std::vector<std::string> messages;
        auto reader = std::make_unique<NativePipeReader>(Collect(messages), 4096);
        Check(reader->Initialize(), "reader initialised");
        Check(reader->Start(client) == NativePipeReader::StartResult::Pending, "read pending");

        // No data ever arrives, so a zero-millisecond wait cannot prove that the
        // read left the I/O manager. The reader must prefer a leak to a
        // use-after-free.
        const bool cancelled = reader->CancelPending(client, 0);
        Check(cancelled || (reader->Abandoned() && reader->LastError() == ERROR_TIMEOUT),
            "an unreaped cancel must abandon instead of freeing in-flight memory");
        Check(!reader->HasPendingRead(), "a cancelled or abandoned read is no longer pending");

        const ULONGLONG destroyed = GetTickCount64();
        reader.reset();
        Check(GetTickCount64() - destroyed < 1000, "destroying a reader must not block on I/O");
        server.Write("{\"n\":1}\n");
        CloseHandle(client);
        Check(messages.empty(), "an abandoned read never delivers its bytes");
    }

    // ---- The removed implementation, kept only as the regression witness ----

    class LegacyConsumer
    {
    public:
        LegacyConsumer()
        {
            event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            buffer.resize(64 * 1024);
            operation.hEvent = event;
        }

        ~LegacyConsumer() { CloseHandle(event); }

        // One iteration of the removed NativeMediaHost.cpp loop: ReadFile, then
        // always reap the completion event, even for a synchronous completion.
        bool Read(HANDLE pipe)
        {
            ResetEvent(event);
            DWORD received{};
            const BOOL ok = ReadFile(pipe, buffer.data(),
                static_cast<DWORD>(buffer.size()), &received, &operation);
            if (ok)
            {
                if (!received) return false;
                Chunk(buffer.data(), received);
            }
            else if (GetLastError() != ERROR_IO_PENDING)
            {
                return false;
            }
            HANDLE waits[1]{ event };
            if (WaitForMultipleObjects(1, waits, FALSE, 5000) != WAIT_OBJECT_0) return false;
            DWORD again{};
            if (!GetOverlappedResult(pipe, &operation, &again, FALSE)) return false;
            if (!again) return false;
            Chunk(buffer.data(), again);
            return true;
        }

        const std::vector<std::string>& Messages() const { return messages; }

    private:
        void Chunk(const char* data, size_t length)
        {
            partial.append(data, length);
            size_t line;
            while ((line = partial.find('\n')) != std::string::npos)
            {
                messages.push_back(partial.substr(0, line));
                partial.erase(0, line + 1);
            }
        }

        HANDLE event{};
        OVERLAPPED operation{};
        std::vector<char> buffer;
        std::string partial;
        std::vector<std::string> messages;
    };

    void RemovedLoopFailsTheSameFixture()
    {
        ServerPipe server;
        HANDLE client = server.Connect();
        LegacyConsumer legacy;
        server.Write(kFirstFragment);
        Check(legacy.Read(client), "the removed loop consumed the first fragment");
        server.Write(kSecondFragment);
        Check(legacy.Read(client), "the removed loop consumed the second fragment");
        Check(legacy.Messages() != kInitialize,
            "the removed loop must not pass the reassembly fixture");
        Check(legacy.Messages().size() == 2,
            "the removed loop frames the duplicated fragment and its duplicate");
        Check(legacy.Messages()[0] ==
            "{\"version\":1,\"ty{\"version\":1,\"type\":\"initialize\",\"renderVersion\":2}",
            "the removed loop reproduces the field corruption exactly");
        CloseHandle(client);
    }
}

int main()
{
    struct Test
    {
        const char* name;
        void (*run)();
    };
    const Test tests[]
    {
        { "A sync completion", SyncCompletionDeliversFragmentsOnce },
        { "B pending completion", PendingCompletionDeliversFragmentsOnce },
        { "C alternating completions", AlternatingCompletionsReuseState },
        { "D many messages in one read", MultipleMessagesInOneRead },
        { "D trailing LF", TrailingLfArrivesSeparately },
        { "D split UTF-8", Utf8SplitInsideCodePoint },
        { "D byte at a time", ByteAtATime },
        { "D message over read buffer", LargeMessageAcrossReadBuffer },
        { "D message limit boundaries", MessageLimitHonoursBoundaries },
        { "D product limit accepts 4 MiB", ProductLimitAcceptsFourMegabyteMessage },
        { "D product limit rejects oversize", ProductLimitRejectsOversizedMessage },
        { "E disconnect while pending", DisconnectWhilePending },
        { "E cancel pending read", CancelPendingReadCompletes },
        { "E cancel racing completion", CancelRacingCompletionDoesNotDeadlock },
        { "E reconnect isolation", ReconnectDoesNotReuseStaleState },
        { "E idle read without stale signal", IdleReadDoesNotInheritStaleSignal },
        { "E wake keeps pending read", WakePreservesPendingRead },
        { "E pump resumes after timeout", PumpResumesAfterTimeout },
        { "E invalid calls fail fast", InvalidCallsFailFast },
        { "E unreaped cancel abandons in-flight memory", UnreapedCancelAbandonsInFlightMemory },
        { "witness removed loop corrupts fragments", RemovedLoopFailsTheSameFixture },
    };

    int failed = 0;
    for (const auto& test : tests)
    {
        try
        {
            test.run();
            std::cout << "PASS " << test.name << "\n";
        }
        catch (const std::exception& error)
        {
            ++failed;
            std::cout << "FAIL " << test.name << ": " << error.what() << "\n";
        }
    }
    std::cout << (failed ? "Native pipe transport tests failed.\n"
                         : "Native pipe transport tests passed.\n");
    return failed ? 1 : 0;
}
