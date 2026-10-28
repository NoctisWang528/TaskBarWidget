#pragma once
#include <Windows.h>
#include <dispatcherqueue.h>
#include <winrt/Windows.System.h>
#include <atomic>

namespace Cards
{
    // Bootstrap is process-wide and owned by the native root (or probe host).
    // A card STA only owns its dispatcher; stopping/recreating cards must never
    // tear down or reinitialize the process's Windows App SDK package graph.
    class Runtime
    {
    public:
        HRESULT Initialize()
        {
            HRESULT result = S_OK;
            if (!winrt::Windows::System::DispatcherQueue::GetForCurrentThread())
            {
                DispatcherQueueOptions options{ sizeof(options), DQTYPE_THREAD_CURRENT, DQTAT_COM_NONE };
                result = CreateDispatcherQueueController(options,
                    reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(dispatcher)));
            }
            return result;
        }
        ~Runtime()
        {
            if (dispatcher)
            {
                auto operation = dispatcher.ShutdownQueueAsync();
                std::atomic<bool> completed{ false };
                operation.Completed([&](auto const&, auto) { completed = true; });
                while (!completed)
                {
                    MSG message{};
                    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                    {
                        if (message.message != WM_QUIT) { TranslateMessage(&message); DispatchMessageW(&message); }
                    }
                    if (!completed) MsgWaitForMultipleObjectsEx(0, nullptr, 10, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                }
                dispatcher = nullptr;
            }
            winrt::clear_factory_cache();
        }
    private:
        winrt::Windows::System::DispatcherQueueController dispatcher{ nullptr };
    };
}
