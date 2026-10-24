#include "CardPch.h"
#include "CardClient.h"
#include "CardText.h"
#include "CardProtocol.h"

#include <winrt/Windows.Data.Json.h>

#include "../TrafficMonitor/NativePipeReader.h"

#include <chrono>

namespace Cards
{
    namespace
    {
        constexpr std::size_t MaximumMessage = 4 * 1024 * 1024;
        constexpr std::size_t MaximumOutgoing = 64;
        constexpr DWORD ReconnectDelay = 400;

        std::wstring PipeName()
        {
            return L"\\\\.\\pipe\\TaskBarWidgets.Cards." +
                std::to_wstring(GetCurrentProcessId());
        }
    }

    Client::~Client() { Stop(); }

    void Client::Start(HWND window)
    {
        if (worker.joinable()) return;
        notify = window;
        wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!wake) return;
        stopping = false;
        worker = std::thread([this] { Run(); });
    }

    void Client::Stop()
    {
        {
            std::lock_guard lock(mutex);
            stopping = true;
        }
        spaceAvailable.notify_all();
        if (wake) SetEvent(wake);
        if (worker.joinable()) worker.join();
        if (wake) { CloseHandle(wake); wake = nullptr; }
        std::lock_guard lock(mutex);
        outgoing.clear();
        incoming.clear();
    }

    std::wstring Client::Request(const std::wstring& action, const std::string& payload,
        const wchar_t* kind)
    {
        const unsigned long long id = ++sequence;
        wchar_t identifier[64]{};
        swprintf_s(identifier, L"%s%llu", kind ? kind : L"misc", id);
        std::string message;
        try
        {
            using namespace winrt::Windows::Data::Json;
            const JsonObject arguments = payload.empty() ? JsonObject{} : JsonObject::Parse(Utf8ToWide(payload));
            // The route and the arguments are separate objects: media commands
            // have their own action (playPause/seek/selectSource) inside payload.
            JsonObject body;
            body.Insert(L"action", JsonValue::CreateStringValue(action));
            body.Insert(L"payload", arguments);
            JsonObject envelope;
            envelope.Insert(L"version", JsonValue::CreateNumberValue(1));
            envelope.Insert(L"type", JsonValue::CreateStringValue(L"command"));
            envelope.Insert(L"action", JsonValue::CreateStringValue(L"cardRequest"));
            envelope.Insert(L"requestId", JsonValue::CreateStringValue(identifier));
            envelope.Insert(L"payload", body);
            message = WideToUtf8(std::wstring(envelope.Stringify().c_str())) + "\n";
        }
        catch (...) { return {}; }
        if (message.size() > MaximumMessage) return {};
        {
            std::lock_guard lock(mutex);
            if (stopping || outgoing.size() >= MaximumOutgoing) return {};
            outgoing.push_back({ std::move(message), identifier });
        }
        if (wake) SetEvent(wake);
        return identifier;
    }

    std::vector<std::string> Client::TakeMessages()
    {
        std::deque<std::string> taken;
        {
            std::lock_guard lock(mutex);
            taken.swap(incoming);
        }
        spaceAvailable.notify_one();
        return std::vector<std::string>(taken.begin(), taken.end());
    }

    void Client::Enqueue(std::string message)
    {
        {
            std::unique_lock lock(mutex);
            spaceAvailable.wait(lock, [this] { return stopping || incoming.size() < 128; });
            if (stopping) return;
            incoming.push_back(std::move(message));
        }
        if (notify) PostMessageW(notify, WM_CARD_PIPE_WAKE, 0, 0);
    }

    bool Client::Write(HANDLE pipe)
    {
        Pending pending;
        {
            std::lock_guard lock(mutex);
            if (outgoing.empty()) return true;
            pending = std::move(outgoing.front());
            outgoing.pop_front();
        }
        const auto fail = [this, &pending]
        {
            // A partial write must never be retried: a mutation may already have
            // executed. Unsent requests remain in the queue for reconnection.
            const auto reply = L"{\"version\":1,\"type\":\"command\",\"action\":\"cardReply\",\"requestId\":" +
                Protocol::JsonString(pending.requestId) +
                L",\"success\":false,\"error\":\"Card pipe write failed; reconnecting.\"}";
            Enqueue(WideToUtf8(reply));
            return false;
        };
        // Drain reads between writes so a large reply cannot deadlock against
        // the server waiting for room while both pipe directions are full.
        const auto& message = pending.message;
        size_t offset = 0;
        while (offset < message.size())
        {
            OVERLAPPED operation{};
            operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!operation.hEvent) return fail();
            DWORD written{};
            BOOL ok = WriteFile(pipe, message.data() + offset,
                static_cast<DWORD>(message.size() - offset), &written, &operation);
            if (!ok && GetLastError() == ERROR_IO_PENDING)
            {
                const DWORD wait = WaitForSingleObject(operation.hEvent, 500);
                if (wait != WAIT_OBJECT_0)
                {
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &written, TRUE);
                    CloseHandle(operation.hEvent);
                    return fail();
                }
                ok = GetOverlappedResult(pipe, &operation, &written, FALSE);
            }
            CloseHandle(operation.hEvent);
            if (!ok || written == 0) return fail();
            offset += written;
        }
        return true;
    }

    void Client::Run()
    {
        const std::wstring name = PipeName();
        while (!stopping)
        {
            HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT |
                    SECURITY_IDENTIFICATION, nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                WaitForSingleObject(wake, ReconnectDelay);
                continue;
            }
            connected = true;
            Enqueue("{\"version\":1,\"type\":\"command\",\"action\":\"connectionStarted\"}");
            NativePipeReader reader([this](std::string message) { Enqueue(std::move(message)); },
                MaximumMessage);
            bool alive = reader.Initialize();
            while (alive && !stopping)
            {
                if (!Write(pipe)) { alive = false; break; }
                DWORD readWait = INFINITE;
                {
                    std::lock_guard lock(mutex);
                    if (!outgoing.empty()) readWait = 0;
                }
                switch (reader.Pump(pipe, wake, readWait))
                {
                case NativePipeReader::PumpResult::Data:
                case NativePipeReader::PumpResult::Woken:
                case NativePipeReader::PumpResult::TimedOut:
                    break;
                case NativePipeReader::PumpResult::Failed:
                    alive = false;
                    break;
                }
            }
            reader.CancelPending(pipe);
            connected = false;
            CloseHandle(pipe);
            if (!stopping) WaitForSingleObject(wake, ReconnectDelay);
        }
    }
}
