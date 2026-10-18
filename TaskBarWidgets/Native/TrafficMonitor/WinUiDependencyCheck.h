#pragma once
#include <Windows.h>
#include <appmodel.h>
#include <filesystem>
#include <WindowsAppSDK-VersionInfo.h>

// Use the same version constants as the managed bootstrapper, from its pinned NuGet package.
// Options=0 means no install UI, automatic installation, fail-fast or debugger interaction.
inline HRESULT CheckWinUiRuntime(const std::filesystem::path& bootstrapPath,
    UINT32 release = WINDOWSAPPSDK_RELEASE_MAJORMINOR,
    UINT64 minimum = WINDOWSAPPSDK_RUNTIME_VERSION_UINT64)
{
    HMODULE bootstrap = LoadLibraryExW(bootstrapPath.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!bootstrap) return HRESULT_FROM_WIN32(GetLastError());
    using Initialize = HRESULT(WINAPI*)(UINT32, PCWSTR, PACKAGE_VERSION, UINT32);
    using Shutdown = void(WINAPI*)();
    const auto initialize = reinterpret_cast<Initialize>(GetProcAddress(bootstrap, "MddBootstrapInitialize2"));
    const auto shutdown = reinterpret_cast<Shutdown>(GetProcAddress(bootstrap, "MddBootstrapShutdown"));
    HRESULT result = HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    if (initialize && shutdown)
    {
        PACKAGE_VERSION version{}; version.Version = minimum;
        result = initialize(release, WINDOWSAPPSDK_RELEASE_VERSION_TAG_W, version, 0);
        if (SUCCEEDED(result)) shutdown();
    }
    FreeLibrary(bootstrap);
    return result;
}
