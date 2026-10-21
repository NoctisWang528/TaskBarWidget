// Cross-language transport client for the media pipe integration test.
//
// It connects to a real TaskBarWidgets MediaPipeServer (hosted by the managed
// test) with the product's own overlapped reader, checks the initialize /
// snapshot / command-reply / delta-frame handshake, then closes its end and
// repeats the handshake on the rebuilt connection. It never starts the product,
// never reads audio, and only touches the uniquely named pipe it is given.

#include "../TrafficMonitor/NativePipeReader.h"

#include <Windows.h>
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>

#include <cstdio>
#include <deque>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <utility>

using namespace winrt::Windows::Data::Json;

namespace
{
    constexpr size_t MaximumMessage = 4 * 1024 * 1024;

    [[noreturn]] void Fail(const std::string& message)
    {
        throw std::runtime_error(message);
    }

    void Require(bool condition, const char* message)
    {
        if (!condition) Fail(message);
    }

    std::string Hex(DWORD value)
    {
        char text[16]{};
        sprintf_s(text, "0x%08lX", static_cast<unsigned long>(value));
        return text;
    }

    std::string Text(const JsonObject& object, const wchar_t* key)
    {
        return winrt::to_string(object.GetNamedString(key, L""));
    }

    double Number(const JsonObject& object, const wchar_t* key)
    {
        return object.GetNamedNumber(key, 0);
    }

    bool Flag(const JsonObject& object, const wchar_t* key)
    {
        return object.GetNamedBoolean(key, false);
    }

    bool IsNullOrMissing(const JsonObject& object, const wchar_t* key)
    {
        if (!object.HasKey(key)) return true;
        return object.GetNamedValue(key).ValueType() == JsonValueType::Null;
    }

    std::wstring Argument(int count, wchar_t** values, const wchar_t* name)
    {
        for (int index = 1; index + 1 < count; ++index)
        {
            if (std::wstring(values[index]) == name) return values[index + 1];
        }
        return {};
    }

    // One accepted connection: the product reader plus its framed messages.
    class Connection
    {
    public:
        Connection(HANDLE pipe, ULONGLONG deadline)
            : pipe(pipe), deadline(deadline)
        {
            reader = std::make_unique<NativePipeReader>(
                [this](std::string message) { messages.push_back(std::move(message)); },
                MaximumMessage);
            if (!reader->Initialize()) Fail("the native reader could not initialise");
        }

        ~Connection() { Close(); }

        Connection(const Connection&) = delete;
        Connection& operator=(const Connection&) = delete;

        void Close()
        {
            if (pipe != INVALID_HANDLE_VALUE)
            {
                CloseHandle(pipe);
                pipe = INVALID_HANDLE_VALUE;
            }
        }

        JsonObject Next()
        {
            for (;;)
            {
                if (GetTickCount64() > deadline) Fail("timed out waiting for a pipe message");
                switch (reader->Pump(pipe, nullptr, 1000))
                {
                case NativePipeReader::PumpResult::Data:
                    break;
                case NativePipeReader::PumpResult::Woken:
                    break;
                case NativePipeReader::PumpResult::TimedOut:
                    continue;
                case NativePipeReader::PumpResult::Failed:
                    Fail("the native reader retired the connection (Win32 " +
                        Hex(reader->LastError()) + ")");
                }
                if (!messages.empty())
                {
                    std::string raw = std::move(messages.front());
                    messages.pop_front();
                    return JsonObject::Parse(winrt::to_hstring(raw));
                }
            }
        }

        void Send(const char* json)
        {
            std::string data(json);
            data.push_back('\n');
            OVERLAPPED operation{};
            HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!event) Fail("cannot create the write completion event");
            size_t offset{};
            while (offset < data.size())
            {
                ZeroMemory(&operation, sizeof(operation));
                operation.hEvent = event;
                DWORD sent{};
                BOOL written = WriteFile(pipe, data.data() + offset,
                    static_cast<DWORD>(data.size() - offset), &sent, &operation);
                if (!written)
                {
                    if (GetLastError() != ERROR_IO_PENDING) Fail("WriteFile failed with Win32 " + Hex(GetLastError()));
                    const DWORD waited = WaitForSingleObject(operation.hEvent, 5000);
                    if (waited != WAIT_OBJECT_0) Fail("the write did not complete");
                    if (!GetOverlappedResult(pipe, &operation, &sent, FALSE))
                        Fail("GetOverlappedResult(write) failed with Win32 " + Hex(GetLastError()));
                }
                if (!sent) Fail("the pipe accepted no bytes");
                offset += sent;
            }
            CloseHandle(event);
        }

    private:
        HANDLE pipe;
        std::deque<std::string> messages;
        std::unique_ptr<NativePipeReader> reader;
        ULONGLONG deadline;
    };

    class Client
    {
    public:
        Client(std::wstring pipeName, DWORD serverPid, DWORD timeoutMilliseconds)
            : pipeName(std::move(pipeName)),
              serverPid(serverPid),
              deadline(GetTickCount64() + timeoutMilliseconds)
        {
        }

        void Run()
        {
            {
                Connection connection(Connect(), deadline);
                JsonObject initialize = connection.Next();
                Require(Text(initialize, L"type") == "initialize",
                    "the first message must be initialize");
                Require(Number(initialize, L"version") == 1, "initialize version");
                Require(Number(initialize, L"renderVersion") == 2, "initialize render version");
                std::cout << "PASS initialize\n";

                JsonObject frame = connection.Next();
                Require(Text(frame, L"type") == "frame", "the second message must be the first frame");
                Require(Number(frame, L"revision") == 1, "the first frame revision");
                Require(!IsNullOrMissing(frame, L"style"), "the first frame must carry its style");
                Require(!IsNullOrMissing(frame, L"cover"), "the first frame must carry its cover");
                const JsonObject style = frame.GetNamedObject(L"style");
                Require(!Text(style, L"fontFamily").empty(), "the style must carry a font family");
                Require(Number(style, L"fontSize") > 0, "the style must carry a font size");
                const JsonObject cover = frame.GetNamedObject(L"cover");
                Require(Text(cover, L"dataUri").rfind("data:image/png;base64,", 0) == 0,
                    "the cover must carry its data URI");
                Require(!Text(cover, L"trackId").empty(), "the cover must carry its track id");
                const JsonObject lyrics = frame.GetNamedObject(L"lyrics");
                Require(Text(lyrics, L"current") == "first line", "the first frame lyrics");
                std::cout << "PASS first frame with style and cover\n";

                connection.Send("{\"version\":1,\"type\":\"command\",\"action\":\"pipeTestEcho\",\"requestId\":\"req-1\"}");
                JsonObject reply = connection.Next();
                Require(Text(reply, L"type") == "command", "the reply must be a command");
                Require(Text(reply, L"action") == "pipeTestReply", "the reply action");
                Require(Text(reply, L"requestId") == "req-1", "the reply request id");
                Require(Flag(reply, L"success"), "the reply success flag");
                std::cout << "PASS command round trip\n";

                connection.Send("{\"version\":1,\"type\":\"command\",\"action\":\"pipeTestPublish\",\"requestId\":\"req-2\"}");
                JsonObject delta = connection.Next();
                Require(Number(delta, L"revision") == 2, "the published frame must arrive");
                Require(IsNullOrMissing(delta, L"style"), "the delta must not resend the style");
                Require(IsNullOrMissing(delta, L"cover"), "the delta must not resend the cover");
                Require(Text(delta.GetNamedObject(L"lyrics"), L"current") == "second line",
                    "the delta lyrics");
                std::cout << "PASS delta frame\n";

                connection.Send("{\"version\":1,\"type\":\"command\",\"action\":\"pipeTestDisconnect\",\"requestId\":\"req-3\"}");
                connection.Close();
                std::cout << "PASS connection closed\n";
            }

            // The server rebuilds the pipe; the same product reader must frame the
            // new connection's first messages.
            Connection rebuilt(Connect(), deadline);
            JsonObject reinitialize = rebuilt.Next();
            Require(Text(reinitialize, L"type") == "initialize",
                "the rebuilt connection must renegotiate");
            JsonObject resnapshot = rebuilt.Next();
            Require(Number(resnapshot, L"revision") == 1, "the rebuilt connection must resend the snapshot");
            Require(!IsNullOrMissing(resnapshot, L"style") && !IsNullOrMissing(resnapshot, L"cover"),
                "the rebuilt connection must resend style and cover");
            rebuilt.Send("{\"version\":1,\"type\":\"command\",\"action\":\"pipeTestDone\",\"requestId\":\"req-4\"}");
            std::cout << "PASS reconnected handshake\n";
            Sleep(150);
        }

    private:
        HANDLE Connect()
        {
            const std::wstring path = L"\\\\.\\pipe\\" + pipeName;
            for (;;)
            {
                if (GetTickCount64() > deadline) Fail("timed out waiting for the named pipe server");
                HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                    nullptr, OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                    nullptr);
                if (pipe == INVALID_HANDLE_VALUE)
                {
                    const DWORD error = GetLastError();
                    if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY)
                        Fail("CreateFileW failed with Win32 " + Hex(error));
                    Sleep(20);
                    continue;
                }
                ULONG pid{};
                if (!GetNamedPipeServerProcessId(pipe, &pid) || pid != serverPid)
                {
                    CloseHandle(pipe);
                    Fail("the pipe server is not the test process");
                }
                std::cout << "PASS connected\n";
                return pipe;
            }
        }

        std::wstring pipeName;
        DWORD serverPid;
        ULONGLONG deadline;
    };
}

int wmain(int argc, wchar_t** argv)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    try
    {
        const std::wstring pipe = Argument(argc, argv, L"--pipe");
        const std::wstring server = Argument(argc, argv, L"--server-pid");
        if (pipe.empty() || server.empty()) Fail("--pipe and --server-pid are required");
        const DWORD timeoutMilliseconds =
            static_cast<DWORD>(std::stoul(Argument(argc, argv, L"--timeout").empty()
                ? std::wstring(L"30000") : Argument(argc, argv, L"--timeout")));
        Client client(pipe, static_cast<DWORD>(std::stoul(server)), timeoutMilliseconds);
        client.Run();
        std::cout << "NATIVE-PIPE-CLIENT OK\n";
        return 0;
    }
    catch (const winrt::hresult_error& error)
    {
        char code[16]{};
        sprintf_s(code, "0x%08lX", static_cast<unsigned long>(error.code()));
        std::cout << "NATIVE-PIPE-CLIENT FAIL " << winrt::to_string(error.message())
                  << " (" << code << ")\n";
        return 1;
    }
    catch (const std::exception& error)
    {
        std::cout << "NATIVE-PIPE-CLIENT FAIL " << error.what() << "\n";
        return 1;
    }
}
