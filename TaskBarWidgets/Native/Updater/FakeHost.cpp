// Isolated test fixture. Never packaged or used by the product.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "../Builtins/utilities/yyjson/yyjson.h"
#include <filesystem>
#include <fstream>
#include <shellapi.h>
#include <string>
namespace fs = std::filesystem;
std::wstring W(const char *s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    w.pop_back();
    return w;
}
std::string Read(const fs::path &p)
{
    std::ifstream f(p, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(f), {});
}
void Write(const fs::path &p, const std::string &text)
{
    fs::create_directories(p.parent_path());
    std::ofstream f(p, std::ios::binary);
    f << text;
}
LRESULT CALLBACK Window(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    if (msg == WM_CLOSE)
    {
        DestroyWindow(hwnd);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, w, l);
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    int count = 0;
    auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (count == 3 && wcscmp(args[1], L"--old") == 0)
    {
        Sleep((DWORD)_wtoi(args[2]));
        LocalFree(args);
        return 0;
    }
    if (count != 4 || wcscmp(args[1], L"--update-health") != 0)
    {
        LocalFree(args);
        return 3;
    }
    auto json = Read(args[2]);
    auto doc = yyjson_read(json.data(), json.size(), 0);
    if (!doc)
    {
        LocalFree(args);
        return 4;
    }
    auto root = yyjson_doc_get_root(doc);
    auto work = fs::path(W(yyjson_get_str(yyjson_obj_get(root, "workRoot"))));
    auto data = fs::path(W(yyjson_get_str(yyjson_obj_get(root, "mediaDataRoot"))));
    std::string nonce = yyjson_get_str(yyjson_obj_get(root, "nonce"));
    std::string version = yyjson_get_str(yyjson_obj_get(root, "version"));
    auto mode = Read(work / L"test-mode.txt");
    if (mode == "fail-data")
    {
        Write(data / L"media-settings.json", "new settings");
        Write(data / L"database/user_data.db", "new db");
        Write(data / L"database/user_data.db-wal", "new wal");
        yyjson_doc_free(doc);
        LocalFree(args);
        return 9;
    }
    if (mode != "no-health")
        Write(work / L"health.json", "{\"ready\":true,\"nonce\":\"" + nonce + "\",\"version\":\"" + version + "\"}");
    WNDCLASSW cls{};
    cls.hInstance = instance;
    cls.lpfnWndProc = Window;
    cls.lpszClassName = L"TaskBarWidgets.Updater.TestHost";
    RegisterClassW(&cls);
    auto window = CreateWindowW(cls.lpszClassName, L"Updater isolated fixture", 0, 0, 0, 0, 0, nullptr, nullptr,
                                instance, nullptr);
    SetTimer(window, 1, 4000, nullptr);
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        if (msg.message == WM_TIMER)
            PostMessageW(window, WM_CLOSE, 0, 0);
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    yyjson_doc_free(doc);
    LocalFree(args);
    return 0;
}
