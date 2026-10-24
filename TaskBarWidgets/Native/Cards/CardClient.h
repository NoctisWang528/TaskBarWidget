#pragma once
// Card pipe client.
//
// Media is the server for TaskBarWidgets.Cards.<nativePid>. Reads happen on a
// dedicated worker thread; every received message is queued and the card UI
// thread is woken with a posted message so parsing, layout and painting never
// race with pipe I/O.

#include <Windows.h>

#include <atomic>
#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

class NativePipeReader;

namespace Cards
{
    constexpr UINT WM_CARD_PIPE_WAKE = WM_APP + 0x210;

    class Client
    {
    public:
        Client() = default;
        ~Client();

        Client(const Client&) = delete;
        Client& operator=(const Client&) = delete;

        // Starts the worker. Posts WM_CARD_PIPE_WAKE to \`notify\` whenever new
        // messages arrive. Reconnection is automatic. Each connection first queues
        // the local connectionStarted event before delivering server messages.
        void Start(HWND notify);
        void Stop();

        // Queues { action, payload: { ...arguments } }. `payload` is a complete
        // JSON argument object (or empty), including the inner media `action`.
        // Returns the request id, or empty on invalid payload / full queue / stop.
        // \`kind\` prefixes the request id so the host can route the reply
        // (audio / traffic / weather / media / update / state).
        std::wstring Request(const std::wstring& action, const std::string& payload = {},
            const wchar_t* kind = L"misc");

        // Drains received messages. Only called on the card UI thread.
        std::vector<std::string> TakeMessages();

        bool Connected() const { return connected.load(); }

    private:
        void Run();
        bool Write(HANDLE pipe);
        void Enqueue(std::string message);

        HWND notify{};
        HANDLE wake{};
        std::thread worker;
        mutable std::mutex mutex;
        struct Pending
        {
            std::string message;
            std::wstring requestId;
        };
        std::condition_variable spaceAvailable;
        std::deque<Pending> outgoing;
        std::deque<std::string> incoming;
        std::atomic<bool> connected{ false };
        std::atomic<bool> stopping{ false };
        std::atomic<unsigned long long> sequence{ 0 };
    };
}
