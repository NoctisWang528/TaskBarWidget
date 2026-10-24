#pragma once
#include <Windows.h>
#include <appmodel.h>
#include <dispatcherqueue.h>
#include <WindowsAppSDK-VersionInfo.h>
#include <winrt/Windows.System.h>
#include <filesystem>
#include <atomic>

namespace Cards
{
    // The preflight releases its bootstrap reference. Actual Mica activation
    // needs a live package graph and dispatcher for the entire card lifetime.
    class Runtime
    {
    public:
        HRESULT Initialize()
        {
            wchar_t path[32768]{};
            if (!GetModuleFileNameW(nullptr, path, ARRAYSIZE(path))) return HRESULT_FROM_WIN32(GetLastError());
            const auto library = std::filesystem::path(path).parent_path() / L"UI" / L"Microsoft.WindowsAppRuntime.Bootstrap.dll";
            module = LoadLibraryExW(library.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (!module) return HRESULT_FROM_WIN32(GetLastError());
            using InitializeFn = HRESULT(WINAPI*)(UINT32, PCWSTR, PACKAGE_VERSION, UINT32);
            const auto initialize = reinterpret_cast<InitializeFn>(GetProcAddress(module, "MddBootstrapInitialize2"));
            shutdown = reinterpret_cast<void(WINAPI*)()>(GetProcAddress(module, "MddBootstrapShutdown"));
            if (!initialize || !shutdown) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
            PACKAGE_VERSION version{};
            version.Version = WINDOWSAPPSDK_RUNTIME_VERSION_UINT64;
            HRESULT result = initialize(WINDOWSAPPSDK_RELEASE_MAJORMINOR, WINDOWSAPPSDK_RELEASE_VERSION_TAG_W, version, 0);
            initialized = SUCCEEDED(result);
            if (FAILED(result)) return result;
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
            // C++/WinRT caches activation factories across apartments. Release
            // those references before the bootstrap can unload their runtime;
            // a later card STA must not call a factory in an unloaded module.
            if (initialized)
            {
                winrt::clear_factory_cache();
                shutdown();
            }
            if (module) FreeLibrary(module);
        }
    private:
        HMODULE module{};
        void(WINAPI* shutdown)(){};
        bool initialized{};
        winrt::Windows::System::DispatcherQueueController dispatcher{ nullptr };
    };
}
