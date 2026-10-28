#pragma once
#include <Windows.h>
#include <appmodel.h>
#include <filesystem>
#include <WindowsAppSDK-VersionInfo.h>

using WinUiBootstrapInitialize = HRESULT(WINAPI*)(UINT32, PCWSTR, PACKAGE_VERSION, UINT32);

// A delay-loaded runtime dependency can raise SEH rather than return an HRESULT.
// Keep that loader failure on the normal dependency-error path; never swallow AVs.
inline HRESULT InvokeWinUiBootstrap(WinUiBootstrapInitialize initialize, UINT32 release,
    PCWSTR tag, PACKAGE_VERSION version)
{
    __try { return initialize(release, tag, version, 0); }
    __except ((GetExceptionCode() == 0xc06d007e || GetExceptionCode() == 0xc06d007f)
        ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
    {
        return HRESULT_FROM_WIN32(GetExceptionCode() & 0xffff);
    }
}

// Own the bootstrap reference for as long as this process uses Windows App SDK.
// Probing then removing the package graph can leave runtime DLLs loaded without
// their delay-load dependencies (notably FrameworkUdk on newer runtime versions).
class WinUiRuntime
{
public:
    WinUiRuntime() = default;
    ~WinUiRuntime() { Reset(); }
    WinUiRuntime(const WinUiRuntime&) = delete;
    WinUiRuntime& operator=(const WinUiRuntime&) = delete;

    HRESULT Initialize(const std::filesystem::path& bootstrapPath,
        UINT32 release = WINDOWSAPPSDK_RELEASE_MAJORMINOR,
        UINT64 minimum = WINDOWSAPPSDK_RUNTIME_VERSION_UINT64)
    {
        if (bootstrap) return E_UNEXPECTED;
        bootstrap = LoadLibraryExW(bootstrapPath.c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!bootstrap) return HRESULT_FROM_WIN32(GetLastError());
        const auto initialize = reinterpret_cast<WinUiBootstrapInitialize>(GetProcAddress(bootstrap, "MddBootstrapInitialize2"));
        shutdown = reinterpret_cast<void(WINAPI*)()>(GetProcAddress(bootstrap, "MddBootstrapShutdown"));
        HRESULT result = HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        if (initialize && shutdown)
        {
            PACKAGE_VERSION version{}; version.Version = minimum;
            // Options=0: no install UI, fail-fast, debugger or automatic installation.
            result = InvokeWinUiBootstrap(initialize, release, WINDOWSAPPSDK_RELEASE_VERSION_TAG_W, version);
        }
        initialized = SUCCEEDED(result);
        if (!initialized) Reset();
        return result;
    }

    bool Initialized() const { return initialized; }

    void Reset()
    {
        if (initialized) { initialized = false; shutdown(); }
        if (bootstrap) { FreeLibrary(bootstrap); bootstrap = nullptr; }
        shutdown = nullptr;
    }

private:
    HMODULE bootstrap{};
    void(WINAPI* shutdown)(){};
    bool initialized{};
};

// Short-lived probes are only for helpers that do not subsequently use WinUI.
inline HRESULT CheckWinUiRuntime(const std::filesystem::path& bootstrapPath,
    UINT32 release = WINDOWSAPPSDK_RELEASE_MAJORMINOR,
    UINT64 minimum = WINDOWSAPPSDK_RUNTIME_VERSION_UINT64)
{
    WinUiRuntime runtime;
    return runtime.Initialize(bootstrapPath, release, minimum);
}
