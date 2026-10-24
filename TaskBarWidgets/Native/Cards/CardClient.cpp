#include "CardPch.h"
#include "CardClient.h"
#include "CardText.h"

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
        stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!wake || !stopEvent) return;
        stopping = false;
        worker = std::thread([this] { Run(); });
    }

    void Client::Stop()
    {
        stopping = true;
        if (stopEvent) SetEvent(stopEvent);
        if (wake) SetEvent(wake);
        if (worker.joinable()) worker.join();
        if (wake) { CloseHandle(wake); wake = nullptr; }
        if (stopEvent) { CloseHandle(stopEvent); stopEvent = nullptr; }
    }

    std::wstring Client::Request(const std::wstring& action, const std::string& payload)
    {
        const unsigned long long id = ++sequence;
        wchar_t identifier[32]{};
        swprintf_s(identifier, L"c%llu", id);
        std::wstring message = L"{\"version\":1,\"type\":\"command\",\"action\":\"cardRequest\",\"requestId\":\"";
        message += identifier;
        message += L"\",\"payload\":{\"action\":\"";
        for (const wchar_t character : action)
        {
            if (character == L'\\' || character == L'"') message += L'\\';
            message += character;
        }
        message += L"\"";
        if (!payload.empty())
        {
            message += L",";
            message += Utf8ToWide(payload);
        }
        message += L"}}";
        {
            std::lock_guard lock(mutex);
            if (outgoing.size() >= MaximumOutgoing) return {};
            outgoing.push_back(WideToUtf8(message) + "\n");
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
        return std::vector<std::string>(taken.begin(), taken.end());
    }

    void Client::Enqueue(std::string message)
    {
        {
            std::lock_guard lock(mutex);
            if (incoming.size() >= 128) incoming.pop_front();
            incoming.push_back(std::move(message));
        }
        if (notify) PostMessageW(notify, WM_CARD_PIPE_WAKE, 0, 0);
    }

    bool Client::Write(HANDLE pipe)
    {
        std::deque<std::string> messages;
        {
            std::lock_guard lock(mutex);
            messages.swap(outgoing);
        }
        for (const auto& message : messages)
        {
            size_t offset = 0;
            while (offset < message.size())
            {
                OVERLAPPED operation{};
                operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
                if (!operation.hEvent) return false;
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
                        return false;
                    }
                    ok = GetOverlappedResult(pipe, &operation, &written, FALSE);
                }
                CloseHandle(operation.hEvent);
                if (!ok || written == 0) return false;
                offset += written;
            }
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
            NativePipeReader reader([this](std::string message) { Enqueue(std::move(message)); },
                MaximumMessage);
            bool alive = reader.Initialize();
            while (alive && !stopping)
            {
                if (!Write(pipe)) { alive = false; break; }
                switch (reader.Pump(pipe, wake))
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
            if (!stopping) Sleep(ReconnectDelay);
        }
    }
}
