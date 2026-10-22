// Product updater: Win32 + static CRT only. Existing yyjson is MIT licensed; see licenses/.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "../Builtins/utilities/yyjson/yyjson.h"
#include "../TrafficMonitor/DotnetDependencyCheck.h"
#include "../TrafficMonitor/WinUiDependencyCheck.h"
#include "UpdateLock.h"
#include <algorithm>
#include <bcrypt.h>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <set>
#include <shellapi.h>
#include <stdexcept>
#include <string>
#include <tlhelp32.h>
#include <vector>
#pragma comment(lib, "bcrypt.lib")
namespace fs = std::filesystem;
namespace
{
struct Failure : std::runtime_error
{
    using std::runtime_error::runtime_error;
};
void Require(bool ok, const char *text)
{
    if (!ok)
        throw Failure(text);
}
std::wstring Wide(const std::string &s)
{
    if (s.empty())
        return {};
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0);
    Require(n > 0, "Invalid UTF-8");
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), w.data(), n);
    return w;
}
std::string Utf8(const std::wstring &w)
{
    if (w.empty())
        return {};
    int n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    Require(n > 0, "Invalid Unicode");
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}
std::wstring Lower(std::wstring w)
{
    for (auto &c : w)
        c = (wchar_t)towlower(c);
    return w;
}
bool Equal(const fs::path &a, const fs::path &b)
{
    // Filesystem paths may mix slash styles when a relative child is appended.
    return Lower(a.lexically_normal().generic_wstring()) == Lower(b.lexically_normal().generic_wstring());
}
bool Under(const fs::path &child, const fs::path &root)
{
    auto r = Lower(root.wstring());
    if (r.back() != L'\\')
        r += L'\\';
    return Lower(child.wstring()).rfind(r, 0) == 0;
}
void Safe(const fs::path &p)
{
    for (auto q = p; !q.empty(); q = q.parent_path())
    {
        DWORD a = GetFileAttributesW(q.c_str());
        if (a == INVALID_FILE_ATTRIBUTES)
            Require(GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND,
                    "Cannot inspect filesystem path");
        else
            Require(!(a & FILE_ATTRIBUTE_REPARSE_POINT), "Filesystem links are forbidden");
        if (q == q.parent_path())
            break;
    }
}
fs::path Absolute(const std::string &text)
{
    fs::path p(Wide(text));
    Require(p.is_absolute() && p.has_root_name() && p.root_name().wstring().size() == 2,
            "Only local absolute paths are allowed");
    Require(text.find(':', 2) == std::string::npos, "Alternate streams are forbidden");
    for (auto &c : p.relative_path())
    {
        auto component = c.wstring();
        if (component.empty())
            continue; // std::filesystem represents a trailing directory separator as an empty component.
        Require(c != L".." && c != L"." && component.back() != L'.' && component.back() != L' ',
                "Root traversal or ambiguous path is forbidden");
    }
    p = fs::absolute(p).lexically_normal();
    while (p != p.root_path() && p.filename().empty())
        p = p.parent_path();
    Require(p != p.root_path(), "Volume roots are forbidden");
    Safe(p);
    return p;
}
bool Has(const std::set<std::wstring> &values, const std::wstring &s)
{
    return values.count(Lower(s)) != 0;
}
void Relative(const std::string &s, bool program = false)
{
    Require(!s.empty() && s.size() <= 240 && s.front() != '/' && s.back() != '/' && s.find('\\') == std::string::npos,
            "Invalid relative path");
    std::vector<std::wstring> parts;
    size_t at = 0;
    while (at < s.size())
    {
        auto end = s.find('/', at);
        if (end == std::string::npos)
            end = s.size();
        auto w = Wide(s.substr(at, end - at));
        Require(!w.empty() && w != L"." && w != L".." && w.back() != L'.' && w.back() != L' ', "Unsafe path component");
        for (auto c : w)
            Require(c >= 32 && std::wstring(L"<>:\"|?*").find(c) == std::wstring::npos, "Unsafe filename");
        auto stem = Lower(w.substr(0, w.find('.')));
        while (!stem.empty() && stem.back() == L' ')
            stem.pop_back();
        Require(stem != L"con" && stem != L"prn" && stem != L"aux" && stem != L"nul" && stem != L"clock$" &&
                    stem != L"conin$" && stem != L"conout$" &&
                    !(stem.size() == 4 && (stem.rfind(L"com", 0) == 0 || stem.rfind(L"lpt", 0) == 0) &&
                      (iswdigit(stem[3]) || stem[3] == L'¹' || stem[3] == L'²' || stem[3] == L'³')),
                "Device filename forbidden");
        parts.push_back(w);
        at = end + 1;
    }
    if (!program)
        return;
    static const std::set<std::wstring> roots = {L"taskbarwidgets.exe",
                                                 L"taskbarwidgets.updater.exe",
                                                 L"openhardwaremonitorapi.dll",
                                                 L"librehardwaremonitorlib.dll",
                                                 L"build-info.json",
                                                 L"release-manifest.json",
                                                 L"readme.txt",
                                                 L"license",
                                                 L"notice",
                                                 L"third-party-notices.md",
                                                 L"acceptance.md",
                                                 L"concrt140.dll",
                                                 L"msvcp140.dll",
                                                 L"msvcp140_1.dll",
                                                 L"msvcp140_2.dll",
                                                 L"msvcp140_atomic_wait.dll",
                                                 L"msvcp140_codecvt_ids.dll",
                                                 L"vcruntime140.dll",
                                                 L"vcruntime140_1.dll",
                                                 L"vcruntime140_threads.dll",
                                                 L"vccorlib140.dll",
                                                 L"mfc140u.dll"};
    if (parts.size() == 1)
    {
        if (!Has(roots, parts[0]))
            throw Failure("Unmanaged root file: " + s);
        return;
    }
    Require(Has({L"media", L"ui", L"language", L"licenses"}, parts[0]), "Unmanaged program directory");
    const bool settingsResource = Has({L"ui/settings/settingsthemeresources.xbf",
        L"media/web/settings/bridge.js", L"media/web/settings/hotkeys.js", L"media/web/settings/integration.js",
        L"media/web/settings/settings.css", L"media/web/settings/settings.html", L"media/web/settings/settings.js",
        L"media/web/settings/state.js", L"media/web/settings/track-offsets-page.js"}, Wide(s));
    for (size_t i = 1; i < parts.size(); ++i)
        Require(!Has({L"data", L"cache", L"caches", L"logs", L"log", L"history", L"backup", L"backups", L"updates",
                      L"settings", L"config", L"configuration", L"userdata", L"user-data", L"plugins"},
                     parts[i]) || (settingsResource && Lower(parts[i]) == L"settings"),
                "Protected data directory");
    auto name = Lower(parts.back());
    auto ext = Lower(fs::path(name).extension().wstring());
    if (Lower(parts[0]) == L"language")
        Require(parts.size() == 2 && (parts[1] == L"English.ini" || parts[1] == L"Simplified_Chinese.ini"),
                "Unmanaged language file");
    else
        Require(
            !Has({L".ini", L".db", L".db3", L".sqlite", L".sqlite3", L".log", L".dat", L".bak", L".tmp", L".config"},
                 ext) &&
                name.find(L".db-") == std::wstring::npos && name.find(L".sqlite-") == std::wstring::npos &&
                name != L"settings.json" && name != L"media-settings.json" && name != L"appsettings.json" &&
                name != L"config.json" && name.find(L"history") == std::wstring::npos &&
                name.find(L"credentials") == std::wstring::npos,
            "Protected configuration file");
}
fs::path Child(const fs::path &root, const std::string &rel, bool program = false)
{
    Relative(rel, program);
    auto p = (root / fs::path(Wide(rel))).lexically_normal();
    Require(Under(p, root), "Path escaped root");
    Safe(p);
    return p;
}
struct Handle
{
    HANDLE h = INVALID_HANDLE_VALUE;
    Handle() = default;
    explicit Handle(HANDLE h) : h(h)
    {
    }
    ~Handle()
    {
        if (h && h != INVALID_HANDLE_VALUE)
            CloseHandle(h);
    }
    Handle(const Handle &) = delete;
    Handle &operator=(const Handle &) = delete;
    Handle(Handle &&o) noexcept : h(o.h)
    {
        o.h = INVALID_HANDLE_VALUE;
    }
};
std::string Read(const fs::path &p, size_t max = 4 * 1024 * 1024)
{
    Safe(p);
    std::ifstream f(p, std::ios::binary);
    Require((bool)f, "Cannot read JSON");
    f.seekg(0, std::ios::end);
    auto n = f.tellg();
    Require(n >= 0 && (uint64_t)n <= max, "JSON exceeds size limit");
    f.seekg(0);
    std::string s((size_t)n, 0);
    f.read(s.data(), n);
    Require((bool)f, "Incomplete JSON read");
    return s;
}
using Json = std::unique_ptr<yyjson_doc, decltype(&yyjson_doc_free)>;
Json Parse(const fs::path &p, size_t limit = 4 * 1024 * 1024)
{
    auto s = Read(p, limit);
    Json d(yyjson_read(s.data(), s.size(), YYJSON_READ_NOFLAG), yyjson_doc_free);
    Require(d != nullptr, "Invalid JSON");
    return d;
}
yyjson_val *Field(yyjson_val *v, const char *k)
{
    auto p = yyjson_obj_get(v, k);
    Require(p != nullptr, "Missing required JSON field");
    return p;
}
std::string String(yyjson_val *v, const char *k)
{
    auto p = Field(v, k);
    Require(yyjson_is_str(p), "Expected JSON string");
    return yyjson_get_str(p);
}
uint64_t Number(yyjson_val *v, const char *k)
{
    auto p = Field(v, k);
    Require(yyjson_is_uint(p), "Expected unsigned JSON number");
    return yyjson_get_uint(p);
}
bool HashString(const std::string &s)
{
    return s.size() == 64 && std::all_of(s.begin(), s.end(), [](unsigned char c) { return isxdigit(c) != 0; });
}
std::string Sha(const fs::path &p)
{
    Safe(p);
    Handle f(CreateFileW(p.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN,
                         nullptr));
    Require(f.h != INVALID_HANDLE_VALUE, "Cannot hash file");
    BCRYPT_ALG_HANDLE alg{};
    BCRYPT_HASH_HANDLE hash{};
    Require(BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, "Cannot initialize SHA256");
    DWORD bytes = 0, n = 0;
    BCryptGetProperty(alg, BCRYPT_OBJECT_LENGTH, (PUCHAR)&bytes, sizeof(bytes), &n, 0);
    std::vector<UCHAR> object(bytes);
    UCHAR result[32]{};
    auto st = BCryptCreateHash(alg, &hash, object.data(), bytes, nullptr, 0, 0);
    if (st < 0)
    {
        BCryptCloseAlgorithmProvider(alg, 0);
        throw Failure("Cannot create hash");
    }
    char buffer[65536];
    DWORD got = 0;
    bool ok = true;
    while ((ok = ReadFile(f.h, buffer, sizeof(buffer), &got, nullptr)) && got)
        if (BCryptHashData(hash, (PUCHAR)buffer, got, 0) < 0)
        {
            ok = false;
            break;
        }
    ok = ok && BCryptFinishHash(hash, result, sizeof(result), 0) >= 0;
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(alg, 0);
    Require(ok, "Hash read failed");
    static const char hex[] = "0123456789abcdef";
    std::string out;
    for (auto b : result)
    {
        out += hex[b >> 4];
        out += hex[b & 15];
    }
    return out;
}
std::string LowerA(std::string s)
{
    for (auto &c : s)
        c = (char)tolower((unsigned char)c);
    return s;
}
void Atomic(const fs::path &path, const std::string &data)
{
    Safe(path);
    auto temp = path;
    temp += L".tmp";
    Safe(temp);
    Handle h(CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    Require(h.h != INVALID_HANDLE_VALUE, "Cannot write transaction state");
    DWORD n = 0;
    Require(WriteFile(h.h, data.data(), (DWORD)data.size(), &n, nullptr) && n == data.size() && FlushFileBuffers(h.h),
            "Cannot flush transaction state");
    CloseHandle(h.h);
    h.h = INVALID_HANDLE_VALUE;
    Require(MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH),
            "Cannot commit transaction state");
}
std::string Quote(const std::string &s)
{
    std::string o = "\"";
    for (unsigned char c : s)
    {
        if (c == '"' || c == '\\')
        {
            o += '\\';
            o += (char)c;
        }
        else if (c < 32)
        {
            char b[7];
            sprintf_s(b, "\\u%04x", c);
            o += b;
        }
        else
            o += (char)c;
    }
    return o + '"';
}
void Status(const fs::path &work, const std::string &phase, const std::string &message = "")
{
    Atomic(work / L"status.json", "{\"phase\":" + Quote(phase) + ",\"helperPid\":" +
                                      std::to_string(GetCurrentProcessId()) + ",\"message\":" + Quote(message) + "}");
}
struct File
{
    std::string path, hash;
    uint64_t bytes{};
};
struct Job
{
    fs::path file, install, stage, work, data;
    std::string version, nonce, manifestHash;
    DWORD pids[3]{};
    uint64_t windowsBuild{};
    std::string dotnet, winui;
};
Job LoadJob(const fs::path &p)
{
    auto d = Parse(p);
    auto v = yyjson_doc_get_root(d.get());
    Require(Number(v, "protocolVersion") == 2, "Unsupported job protocol");
    Job j;
    j.file = p;
    j.install = Absolute(String(v, "installRoot"));
    j.stage = Absolute(String(v, "stageRoot"));
    j.work = Absolute(String(v, "workRoot"));
    j.data = Absolute(String(v, "dataRoot"));
    Require(Equal(p.parent_path(), j.work), "Job directory must equal workRoot");
    Require(fs::is_directory(j.install) && fs::is_directory(j.stage) && fs::is_directory(j.work),
            "Missing transaction directory");
    Require(!Equal(j.install, j.stage) && !Under(j.stage, j.install) && !Under(j.install, j.stage) &&
                !Equal(j.install, j.work) && !Under(j.work, j.install) && !Under(j.install, j.work) &&
                !Equal(j.stage, j.work) && !Under(j.work, j.stage) && !Under(j.stage, j.work),
            "Program, stage and job roots must be disjoint");
    Require(!Under(j.work, j.data) && !Under(j.data, j.work) && !Equal(j.work, j.data),
            "Data overlaps transaction directory");
#ifndef TBW_UPDATER_TEST
    Require(Equal(j.data, j.install / L"Data"),
            "Unrecognized personal data roots");
#endif
    j.version = String(v, "version");
    Require(
        j.version.size() == 11 && j.version[6] == '-' &&
            std::all_of(j.version.begin(), j.version.end(), [](char c) { return c == '-' || (c >= '0' && c <= '9'); }),
        "Invalid target version");
    j.nonce = String(v, "nonce");
    Require(
        j.nonce.size() >= 16 && j.nonce.size() <= 128 &&
            std::all_of(j.nonce.begin(), j.nonce.end(), [](char c) { return isalnum((unsigned char)c) || c == '-'; }),
        "Invalid health nonce");
    j.manifestHash = String(v, "fileManifestSha256");
    Require(HashString(j.manifestHash), "Invalid manifest hash");
    const char *keys[] = {"nativePid", "mediaPid", "uiPid"};
    for (int i = 0; i < 3; ++i)
    {
        auto n = Number(v, keys[i]);
        Require(n <= MAXDWORD, "Invalid process ID");
        j.pids[i] = (DWORD)n;
    }
    auto r = Field(v, "requirements");
    j.windowsBuild = Number(r, "windowsBuild");
    j.dotnet = String(r, "dotnetDesktop");
    j.winui = String(r, "windowsAppRuntime");
    wchar_t own[32768];
    DWORD len = GetModuleFileNameW(nullptr, own, 32768);
    Require(len && len < 32768 && Under(fs::path(own), j.work), "Updater must run from its job directory");
    return j;
}
std::vector<File> Manifest(const fs::path &p, const std::string &version = "")
{
    auto d = Parse(p);
    auto root = yyjson_doc_get_root(d.get());
    Require(Number(root, "protocolVersion") == 1 && String(root, "architecture") == "win-x64",
            "Unsupported file inventory");
    auto manifestVersion = String(root, "version");
    if (!version.empty())
        Require(manifestVersion == version, "Inventory version mismatch");
    auto a = Field(root, "files");
    Require(yyjson_is_arr(a) && yyjson_arr_size(a) > 0 && yyjson_arr_size(a) < 8192, "Invalid file count");
    std::set<std::wstring> seen;
    std::vector<File> files;
    uint64_t sum = 0;
    size_t idx, max;
    yyjson_val *item;
    yyjson_arr_foreach(a, idx, max, item)
    {
        File f;
        f.path = String(item, "path");
        Relative(f.path, true);
        Require(seen.insert(Lower(Wide(f.path))).second, "Duplicate inventory path");
        f.bytes = Number(item, "bytes");
        f.hash = LowerA(String(item, "sha256"));
        Require(HashString(f.hash) && f.bytes <= 2ull * 1024 * 1024 * 1024, "Invalid file metadata");
        sum += f.bytes;
        Require(sum <= 2ull * 1024 * 1024 * 1024, "Unpacked limit exceeded");
        files.push_back(f);
    }
    std::map<std::wstring, std::wstring> casing;
    for (auto &f : files)
    {
        auto original = Wide(f.path);
        auto path = Lower(original);
        for (auto &other : seen)
            Require(other.rfind(path + L"/", 0) != 0, "Inventory file/directory conflict");
        size_t at = 0;
        while ((at = original.find(L'/', at)) != std::wstring::npos)
        {
            auto prefix = original.substr(0, at++);
            auto found = casing.emplace(Lower(prefix), prefix);
            Require(found.second || found.first->second == prefix, "Inventory directory casing conflict");
        }
    }
    Require(seen.count(L"taskbarwidgets.exe"), "Missing native entry point");
    return files;
}
void VerifyStage(const Job &j, const std::vector<File> &files)
{
    Require(Sha(j.stage / L"app-files.json") == LowerA(j.manifestHash), "File inventory hash mismatch");
    std::set<std::wstring> listed = {L"app-files.json"};
    for (auto &f : files)
    {
        auto p = Child(j.stage, f.path, true);
        Require(fs::is_regular_file(p) && fs::file_size(p) == f.bytes && Sha(p) == f.hash,
                "Staged file hash/size mismatch");
        listed.insert(Lower(Wide(f.path)));
    }
    for (auto &e : fs::recursive_directory_iterator(j.stage))
    {
        Safe(e.path());
        if (e.is_regular_file())
            Require(listed.count(Lower(e.path().lexically_relative(j.stage).generic_wstring())) != 0,
                    "Unlisted staged file");
        else
            Require(e.is_directory(), "Unsupported staged object");
    }
}
void Dependencies(const Job &j)
{
#ifdef TBW_UPDATER_TEST
    // Test-only dependency simulator. Not compiled into the shipped updater.
    Require(j.dotnet != "missing", "Synthetic dependency failure");
    return;
#else
    using VersionFn = LONG(WINAPI *)(OSVERSIONINFOW *);
    OSVERSIONINFOW os{};
    os.dwOSVersionInfoSize = sizeof(os);
    auto fn = (VersionFn)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion");
    Require(fn && fn(&os) == 0 && os.dwBuildNumber >= j.windowsBuild, "Windows build requirement not met");
    auto minDotnet = ParseVersion(Wide(j.dotnet));
    Require(minDotnet.stable && minDotnet.major == 8 && minDotnet.minor == 0, "Unsupported .NET requirement");
    for (auto framework : {L"Microsoft.NETCore.App", L"Microsoft.WindowsDesktop.App"})
    {
        bool compatible = false;
        auto root = SystemDotnetRoot() / L"shared" / framework;
        Require(fs::is_directory(root), "Shared .NET framework missing");
        for (auto &entry : fs::directory_iterator(root))
        {
            auto version = ParseVersion(entry.path().filename().wstring());
            if (entry.is_directory() && version.stable && version.major == minDotnet.major &&
                version.minor == minDotnet.minor && version.patch >= minDotnet.patch)
            {
                compatible = true;
                break;
            }
        }
        Require(compatible, "Shared .NET version is below update requirement");
    }
    for (auto rel : {L"Media/TaskBarWidgets.Media.runtimeconfig.json", L"UI/TaskBarWidgets.UI.runtimeconfig.json"})
    {
        auto result = CheckDotnetRuntime(SystemDotnetRoot(), j.stage / rel);
        Require(result.success, "Required shared .NET runtime unavailable");
    }
    unsigned a = 0, b = 0, c = 0, d = 0;
    wchar_t tail = 0;
    auto ver = Wide(j.winui);
    Require(swscanf_s(ver.c_str(), L"%u.%u.%u.%u%c", &a, &b, &c, &d, &tail, 1u) == 4 && a == 2 && b <= 65535 &&
                c <= 65535 && d <= 65535,
            "Unsupported Windows App Runtime requirement");
    UINT64 min = ((UINT64)a << 48) | ((UINT64)b << 32) | ((UINT64)c << 16) | d;
    Require(SUCCEEDED(CheckWinUiRuntime(j.stage / L"UI/Microsoft.WindowsAppRuntime.Bootstrap.dll", (a << 16) | b, min)),
            "Required shared Windows App Runtime unavailable");
#endif
}
std::wstring Image(HANDLE h)
{
    wchar_t b[32768];
    DWORD n = 32768;
    Require(QueryFullProcessImageNameW(h, 0, b, &n), "Cannot inspect process image");
    return fs::path(std::wstring(b, n)).lexically_normal().wstring();
}
std::vector<Handle> OldProcesses(const Job &j)
{
    std::vector<Handle> out;
    const wchar_t *expected[] = {L"TaskBarWidgets.exe", L"Media/TaskBarWidgets.Media.exe", L"UI/TaskBarWidgets.UI.exe"};
    for (int i = 0; i < 3; ++i)
    {
        if (!j.pids[i])
            continue;
        Handle h(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, j.pids[i]));
        if (h.h == nullptr)
        {
            Require(GetLastError() == ERROR_INVALID_PARAMETER, "Cannot inspect old process");
            continue;
        }
        Require(Equal(Image(h.h), j.install / expected[i]), "Process ID no longer belongs to this installation");
        out.push_back(std::move(h));
    }
    return out;
}
DWORD ExitTimeout()
{
#ifdef TBW_UPDATER_TEST
    return 800;
#else
    return 30000;
#endif
}
bool Wait(std::vector<Handle> &handles, DWORD ms)
{
    ULONGLONG until = GetTickCount64() + ms;
    for (auto &h : handles)
    {
        DWORD wait = GetTickCount64() >= until ? 0 : (DWORD)(until - GetTickCount64());
        if (WaitForSingleObject(h.h, wait) != WAIT_OBJECT_0)
            return false;
    }
    return true;
}
void NotCancelled(const Job &j)
{
    auto request = j.work / L"cancel.request";
    Safe(request);
    Require(!fs::exists(request), "Update was cancelled before installation");
}
bool ApprovedExit(const Job &j, std::vector<Handle> &old)
{
    auto until = GetTickCount64() + ExitTimeout();
    while (GetTickCount64() < until)
    {
        NotCancelled(j);
        auto signal = j.work / L"shutdown-approved.json";
        Safe(signal);
        if (fs::exists(signal))
        {
            try
            {
                auto doc = Parse(signal);
                if (String(yyjson_doc_get_root(doc.get()), "nonce") == j.nonce && Wait(old, 0))
                    return true;
            }
            catch (const Failure &)
            {
            }
        }
        Sleep(50);
    }
    return false;
}
std::vector<Handle> Running(const Job &j)
{
    std::vector<Handle> out;
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    Require(snapshot.h != INVALID_HANDLE_VALUE, "Cannot enumerate processes");
    PROCESSENTRY32W e{sizeof(e)};
    for (BOOL ok = Process32FirstW(snapshot.h, &e); ok; ok = Process32NextW(snapshot.h, &e))
    {
        if (e.th32ProcessID == GetCurrentProcessId())
            continue;
        Handle h(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, e.th32ProcessID));
        if (!h.h)
            continue;
        try
        {
            if (Under(Image(h.h), j.install))
                out.push_back(std::move(h));
        }
        catch (const Failure &)
        {
        }
    }
    return out;
}
void ProbeWrite(const fs::path &root)
{
    Safe(root);
    fs::create_directories(root);
    auto p = root / L".tbw-update-write-probe";
    Handle h(CreateFileW(p.c_str(), GENERIC_WRITE | DELETE, 0, nullptr, CREATE_NEW,
                         FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr));
    Require(h.h != INVALID_HANDLE_VALUE, "Directory is not writable");
}
void Copy(const fs::path &source, const fs::path &target)
{
    Safe(source);
    Safe(target);
    fs::create_directories(target.parent_path());
    Require(CopyFileW(source.c_str(), target.c_str(), FALSE), "Cannot copy backup/program file");
    Handle h(CreateFileW(target.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
                         nullptr));
    Require(h.h != INVALID_HANDLE_VALUE && FlushFileBuffers(h.h), "Cannot flush backup/program file");
}
void Replace(const fs::path &source, const fs::path &target, const std::string &expectedHash)
{
    auto temp = target;
    temp += L".tbw-update-tmp";
    Require(!fs::exists(temp), "Pending replacement file exists");
    Copy(source, temp);
    Require(Sha(temp) == LowerA(expectedHash), "Replacement changed after verification");
    if (!MoveFileExW(temp.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        DeleteFileW(temp.c_str());
        throw Failure("Cannot atomically replace program file");
    }
}
bool NativePersonal(const std::string &rel)
{
    auto p = fs::path(Wide(rel));
    auto n = Lower(p.filename().wstring());
    return n == L"layout.json" ||
           Lower(p.parent_path().wstring()) == L"config" && Lower(p.extension().wstring()) == L".ini" ||
           Lower(p.parent_path().wstring()) == L"history" &&
               (n == L"traffic.dat" || n.rfind(L"traffic.dat.", 0) == 0);
}
bool MediaPersonal(const std::string &rel)
{
    auto s = Lower(Wide(rel));
    auto p = fs::path(Wide(rel));
    auto n = Lower(p.filename().wstring());
    return s == L"config/media.json" ||
           Lower(p.parent_path().wstring()) == L"lyrics" &&
               (Lower(p.extension().wstring()) == L".db" || Lower(p.extension().wstring()) == L".json") ||
           s == L"secrets/weather-appkey.bin";
}
std::vector<std::string> DataFiles(const fs::path &root, bool media)
{
    std::vector<std::string> out;
    if (!fs::exists(root))
        return out;
    Safe(root);
    for (auto &e : fs::recursive_directory_iterator(root))
    {
        Safe(e.path());
        auto rel = Utf8(e.path().lexically_relative(root).generic_wstring());
        if (e.is_regular_file() && (media ? MediaPersonal(rel) : NativePersonal(rel)))
        {
            Relative(rel);
            Require(out.size() < 8192, "Too many personal files");
            out.push_back(rel);
        }
    }
    return out;
}
struct Saved
{
    std::string group, path, hash;
    bool existed = false;
};
struct Journal
{
    std::string phase;
    std::vector<Saved> saved;
    DWORD launched = 0;
};
fs::path Base(const Job &j, const std::string &g)
{
    if (g == "program" || g == "marker")
        return j.install;
    if (g == "native" || g == "media")
        return j.data;
    throw Failure("Invalid journal group");
}
void ValidSaved(const Saved &s)
{
    Relative(s.path);
    if (s.group == "program")
    {
        if (s.path != "app-files.json")
            Relative(s.path, true);
    }
    else if (s.group == "marker")
        Require(false, "Unsafe configuration marker");
    else if (s.group == "native")
        Require(NativePersonal(s.path), "Unsafe native data in journal");
    else if (s.group == "media")
        Require(MediaPersonal(s.path), "Unsafe media data in journal");
    else
        throw Failure("Unknown journal group");
    Require(!s.existed || HashString(s.hash), "Missing backup hash");
}
void SaveJournal(const Job &j, const Journal &journal)
{
    std::string s = "{\"protocolVersion\":1,\"nonce\":" + Quote(j.nonce) + ",\"phase\":" + Quote(journal.phase) +
                    ",\"launchedPid\":" + std::to_string(journal.launched) + ",\"files\":[";
    bool first = true;
    for (auto &f : journal.saved)
    {
        if (!first)
            s += ',';
        first = false;
        s += "{\"group\":" + Quote(f.group) + ",\"path\":" + Quote(f.path) +
             ",\"existed\":" + (f.existed ? "true" : "false") + ",\"sha256\":" + Quote(f.hash) + "}";
    }
    Atomic(j.work / L"journal.json", s + "]}");
}
Journal ReadJournal(const Job &j)
{
    auto d = Parse(j.work / L"journal.json", 16 * 1024 * 1024);
    auto v = yyjson_doc_get_root(d.get());
    Require(Number(v, "protocolVersion") == 1 && String(v, "nonce") == j.nonce, "Journal does not belong to job");
    Journal t;
    t.phase = String(v, "phase");
    Require(t.phase == "backingUp" || t.phase == "prepared" || t.phase == "installing" || t.phase == "installed" ||
                t.phase == "starting" || t.phase == "complete" || t.phase == "rollingBack" || t.phase == "rolledBack",
            "Unknown transaction phase");
    auto pid = Number(v, "launchedPid");
    Require(pid <= MAXDWORD, "Invalid journal process");
    t.launched = (DWORD)pid;
    auto a = Field(v, "files");
    Require(yyjson_is_arr(a) && yyjson_arr_size(a) <= 32768, "Invalid journal inventory");
    size_t i, n;
    yyjson_val *f;
    std::set<std::wstring> seen;
    yyjson_arr_foreach(a, i, n, f)
    {
        Saved s;
        s.group = String(f, "group");
        s.path = String(f, "path");
        auto ex = Field(f, "existed");
        Require(yyjson_is_bool(ex), "Invalid existence flag");
        s.existed = yyjson_get_bool(ex);
        s.hash = String(f, "sha256");
        ValidSaved(s);
        Require(seen.insert(Wide(s.group) + L":" + Lower(Wide(s.path))).second, "Duplicate journal file");
        t.saved.push_back(s);
    }
    Require(!t.saved.empty() || t.phase == "backingUp" || t.phase == "rolledBack", "Empty journal");
    return t;
}
void Snapshot(const Job &j, Journal &t, const std::string &group, const std::string &rel)
{
    Saved s{group, rel, "", false};
    ValidSaved(s);
    auto from = Child(Base(j, group), rel);
    s.existed = fs::exists(from);
    if (s.existed)
    {
        Require(fs::is_regular_file(from), "Backup source is not a file");
        auto to = Child(j.work / L"backup" / Wide(group), rel);
        Copy(from, to);
        s.hash = Sha(to);
        Require(Sha(from) == s.hash, "Backup source changed");
    }
    t.saved.push_back(s);
    SaveJournal(j, t);
}
std::map<std::wstring, std::string> ProgramPaths(const Job &j, const std::vector<File> &next)
{
    std::map<std::wstring, std::string> paths = {{L"app-files.json", "app-files.json"}};
    auto add = [&](const std::string &path) { paths.emplace(Lower(Wide(path)), path); };
    if (fs::exists(j.install / L"app-files.json"))
    {
        for (auto &f : Manifest(j.install / L"app-files.json"))
            add(f.path);
    }
    // Without a prior inventory, only incoming targets are known program files.
    // Unknown files are retained, even inside the permitted program directories.
    for (auto &f : next)
        add(f.path);
    return paths;
}
Journal Backup(const Job &j, const std::vector<File> &next)
{
    Require(!fs::exists(j.work / L"journal.json") && !fs::exists(j.work / L"backup"),
            "Existing transaction requires recovery");
    auto paths = ProgramPaths(j, next);
    Journal t;
    t.phase = "backingUp";
    SaveJournal(j, t);
    for (auto &p : paths)
        Snapshot(j, t, "program", p.second);
    for (auto &p : DataFiles(j.data, false))
        Snapshot(j, t, "native", p);
    for (auto &p : DataFiles(j.data, true))
        Snapshot(j, t, "media", p);
    t.phase = "prepared";
    SaveJournal(j, t);
    return t;
}
void SpaceAndPermissions(const Job &j, const std::vector<File> &files)
{
    uint64_t need = 16 * 1024 * 1024;
    for (auto &f : files)
        need += f.bytes * 3;
    for (auto &entry : ProgramPaths(j, files))
    {
        auto file = Child(j.install, entry.second);
        if (fs::exists(file))
        {
            Require(fs::is_regular_file(file), "Managed backup source is not a file");
            auto bytes = fs::file_size(file);
            Require(bytes <= 2ull * 1024 * 1024 * 1024 && need <= UINT64_MAX - bytes,
                    "Managed backup exceeds limits");
            need += bytes;
        }
    }
    for (auto pair : {std::make_pair(j.data, false), std::make_pair(j.data, true)})
        for (auto &p : DataFiles(pair.first, pair.second))
            need += fs::file_size(Child(pair.first, p));
    ULARGE_INTEGER avail{};
    Require(GetDiskFreeSpaceExW(j.work.c_str(), &avail, nullptr, nullptr) && avail.QuadPart > need,
            "Insufficient backup space");
    Require(GetDiskFreeSpaceExW(j.install.c_str(), &avail, nullptr, nullptr) && avail.QuadPart > need,
            "Insufficient installation space");
    ProbeWrite(j.work);
    ProbeWrite(j.install);
    ProbeWrite(j.data);
}
void UnlockedTargets(const Job &j, const Journal &t)
{
    for (auto &s : t.saved)
    {
        auto p = Child(Base(j, s.group), s.path);
        if (fs::exists(p))
        {
            Handle h(CreateFileW(p.c_str(), GENERIC_READ | DELETE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                 FILE_ATTRIBUTE_NORMAL, nullptr));
            Require(h.h != INVALID_HANDLE_VALUE, "A transaction file is locked");
        }
    }
}
void Apply(const Job &j, const std::vector<File> &files, Journal &t)
{
    t.phase = "installing";
    SaveJournal(j, t);
    std::set<std::wstring> next;
    for (auto &f : files)
        next.insert(Lower(Wide(f.path)));
    for (auto &s : t.saved)
        if (s.group == "program" && s.path != "app-files.json" && !next.count(Lower(Wide(s.path))))
        {
            auto p = Child(j.install, s.path, true);
            if (fs::exists(p))
                Require(DeleteFileW(p.c_str()), "Cannot remove retired program file");
            SaveJournal(j, t);
        }
    for (auto &f : files)
        if (LowerA(f.path) != "taskbarwidgets.exe")
        {
            Replace(Child(j.stage, f.path, true), Child(j.install, f.path, true), f.hash);
            SaveJournal(j, t);
#ifdef TBW_UPDATER_TEST
            if (fs::exists(j.work / L"test-interrupt"))
                ExitProcess(91);
#endif
        }
    Replace(j.stage / L"app-files.json", j.install / L"app-files.json", j.manifestHash);
    for (auto &f : files)
        if (LowerA(f.path) == "taskbarwidgets.exe")
            Replace(Child(j.stage, f.path, true), j.install / L"TaskBarWidgets.exe", f.hash);
    t.phase = "installed";
    SaveJournal(j, t);
}
Handle Launch(const Job &j, bool health, DWORD *pid = nullptr)
{
    std::wstring command = L"\"" + (j.install / L"TaskBarWidgets.exe").wstring() + L"\"";
    if (health)
        command += L" --update-health \"" + j.file.wstring() + L"\" " + Wide(j.nonce);
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    auto env = ManagedEnvironment(SystemDotnetRoot());
    Require(CreateProcessW((j.install / L"TaskBarWidgets.exe").c_str(), command.data(), nullptr, nullptr, FALSE,
                           CREATE_UNICODE_ENVIRONMENT, env.empty() ? nullptr : env.data(), j.install.c_str(), &si, &pi),
            "Cannot launch application");
    CloseHandle(pi.hThread);
    if (pid)
        *pid = pi.dwProcessId;
    return Handle(pi.hProcess);
}
bool Healthy(const Job &j, HANDLE process)
{
#ifdef TBW_UPDATER_TEST
    const DWORD timeout = 1500;
#else
    const DWORD timeout = 60000;
#endif
    auto until = GetTickCount64() + timeout;
    while (GetTickCount64() < until)
    {
        if (WaitForSingleObject(process, 0) == WAIT_OBJECT_0)
            return false;
        if (fs::exists(j.work / L"health.json"))
        {
            try
            {
                auto d = Parse(j.work / L"health.json");
                auto v = yyjson_doc_get_root(d.get());
                auto ready = Field(v, "ready");
                if (String(v, "nonce") == j.nonce && String(v, "version") == j.version && yyjson_is_true(ready))
                    return true;
            }
            catch (const Failure &)
            {
            }
        }
        Sleep(100);
    }
    return false;
}
BOOL CALLBACK CloseWindow(HWND w, LPARAM p)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(w, &pid);
    if (pid == (DWORD)p)
        PostMessageW(w, WM_CLOSE, 0, 0);
    return TRUE;
}
bool StopNew(const Job &j, Journal &t)
{
    if (t.launched)
    {
        Handle h(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, t.launched));
        if (h.h && WaitForSingleObject(h.h, 0) != WAIT_OBJECT_0)
        {
            Require(Equal(Image(h.h), j.install / L"TaskBarWidgets.exe"), "New process ID was reused");
            EnumWindows(CloseWindow, (LPARAM)t.launched);
        }
    }
    auto all = Running(j);
    return Wait(all, ExitTimeout());
}
void Rollback(const Job &j, Journal &t)
{
    Require(Running(j).empty(), "Application is still running; recovery required");
    UnlockedTargets(j, t);
    for (auto &s : t.saved)
        if (s.existed)
            Require(Sha(Child(j.work / L"backup" / Wide(s.group), s.path)) == s.hash, "Rollback backup hash mismatch");
    t.phase = "rollingBack";
    SaveJournal(j, t);
    for (auto &s : t.saved)
    {
        auto temp = Child(Base(j, s.group), s.path);
        temp += L".tbw-update-tmp";
        Safe(temp);
        if (fs::exists(temp))
            Require(fs::is_regular_file(temp) && DeleteFileW(temp.c_str()),
                    "Cannot clear interrupted temporary replacement");
    }
    // Remove newly created settings and database sidecars, then restore the frozen snapshot.
    for (auto group : {std::string("native"), std::string("media")})
        for (auto &rel : DataFiles(Base(j, group), group == "media"))
        {
            auto it = std::find_if(t.saved.begin(), t.saved.end(),
                                   [&](auto &s) { return s.group == group && s.path == rel; });
            if (it == t.saved.end())
                Require(DeleteFileW(Child(Base(j, group), rel).c_str()), "Cannot remove new user data during rollback");
        }
    for (auto &s : t.saved)
    {
        if (s.group == "program" && LowerA(s.path) == "taskbarwidgets.exe")
            continue;
        auto target = Child(Base(j, s.group), s.path);
        if (s.existed)
            Replace(Child(j.work / L"backup" / Wide(s.group), s.path), target, s.hash);
        else if (fs::exists(target))
            Require(DeleteFileW(target.c_str()), "Cannot remove newly installed file");
        SaveJournal(j, t);
    }
    for (auto &s : t.saved)
        if (s.group == "program" && LowerA(s.path) == "taskbarwidgets.exe" && s.existed)
            Replace(Child(j.work / L"backup" / Wide(s.group), s.path), j.install / L"TaskBarWidgets.exe", s.hash);
    t.phase = "rolledBack";
    SaveJournal(j, t);
    Status(j.work, "rolledBack", "Update failed; previous application and data restored");
}
void RestartOld(const Job &j, Handle &lock)
{
#ifndef TBW_UPDATER_TEST
    Require(ReleaseMutex(lock.h), "Cannot release update lock");
    Launch(j, false);
#else
    Atomic(j.work / L"test-old-restarted", "1");
    (void)lock;
#endif
}
void FinishIntactTransaction(const Job &j)
{
    if (!fs::exists(j.work / L"journal.json"))
        return;
    auto journal = ReadJournal(j);
    Require(journal.phase == "backingUp" || journal.phase == "prepared", "Transaction already changed the application");
    journal.phase = "rolledBack"; // No replacements began; the original installation is already intact.
    SaveJournal(j, journal);
}
void RotateOlderBackups(const Job &j)
{
    // Only completed sibling jobs for this installation are eligible. Unknown or
    // unfinished jobs, extra files, links and live helpers retain their backups.
    if (Lower(j.work.parent_path().filename().wstring()) != L"jobs")
        return;
    for (auto &entry : fs::directory_iterator(j.work.parent_path()))
        try
        {
            if (!entry.is_directory() || Equal(entry.path(), j.work))
                continue;
            Safe(entry.path());
            auto document = Parse(entry.path() / L"job.json");
            auto value = yyjson_doc_get_root(document.get());
            if (Number(value, "protocolVersion") != 2 || !Equal(Absolute(String(value, "installRoot")), j.install) ||
                !Equal(Absolute(String(value, "workRoot")), entry.path()))
                continue;
            Job prior = j;
            prior.work = entry.path();
            prior.nonce = String(value, "nonce");
            auto journal = ReadJournal(prior);
            if (journal.phase != "complete")
                continue;
            bool running = false;
            Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
            Require(snapshot.h != INVALID_HANDLE_VALUE, "Cannot inspect old helpers");
            PROCESSENTRY32W process{sizeof(process)};
            for (BOOL ok = Process32FirstW(snapshot.h, &process); ok; ok = Process32NextW(snapshot.h, &process))
                if (_wcsicmp(process.szExeFile, L"TaskBarWidgets.Updater.exe") == 0)
                {
                    Handle h(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process.th32ProcessID));
                    if (!h.h)
                    {
                        running = true;
                        break;
                    }
                    if (Under(Image(h.h), entry.path()))
                    {
                        running = true;
                        break;
                    }
                }
            if (running)
                continue;
            auto backup = entry.path() / L"backup";
            Safe(backup);
            if (!fs::is_directory(backup))
                continue;
            std::set<std::wstring> allowed;
            for (auto &saved : journal.saved)
                if (saved.existed)
                    allowed.insert(Lower(Child(backup / Wide(saved.group), saved.path).wstring()));
            std::vector<fs::path> files, dirs;
            for (auto &item : fs::recursive_directory_iterator(backup))
            {
                Safe(item.path());
                if (item.is_directory())
                    dirs.push_back(item.path());
                else
                {
                    Require(item.is_regular_file() && allowed.count(Lower(item.path().wstring())),
                            "Unrecognized backup content");
                    files.push_back(item.path());
                }
            }
            for (auto &file : files)
                fs::remove(file);
            std::sort(dirs.begin(), dirs.end(), [](auto &a, auto &b) { return a.native().size() > b.native().size(); });
            for (auto &dir : dirs)
                fs::remove(dir);
            fs::remove(backup);
        }
        catch (const std::exception &)
        { /* Retaining an older backup never invalidates a healthy install. */
        }
}
int Run(const Job &j, bool recover)
{
    auto mutexName = TaskBarWidgetsUpdateMutexName(j.install.wstring());
    Require(!mutexName.empty(), "Invalid mutex root");
    Handle lock(CreateMutexW(nullptr, TRUE, mutexName.c_str()));
    Require(lock.h && GetLastError() != ERROR_ALREADY_EXISTS, "An update for this installation is already active");
    if (recover)
    {
        try
        {
            auto t = ReadJournal(j);
            if (t.phase == "complete" || t.phase == "rolledBack")
            {
                Status(j.work, t.phase);
                return 0;
            }
            if (t.phase == "backingUp" || t.phase == "prepared")
            {
                FinishIntactTransaction(j);
                Status(j.work, "rolledBack", "Interrupted before replacement; original installation remains intact");
                if (Running(j).empty())
                    RestartOld(j, lock);
                return 0;
            }
            auto old = OldProcesses(j);
            if (!Wait(old, 0) || !StopNew(j, t))
            {
                Status(j.work, "recoveryRequired", "Close this installation before recovery");
                return 2;
            }
            Rollback(j, t);
            RestartOld(j, lock);
            return 0;
        }
        catch (const std::exception &)
        {
            Status(j.work, "recoveryRequired", "Recovery could not finish safely; journal and backup retained");
            return 2;
        }
    }
    auto old = OldProcesses(j);
    Require(!fs::exists(j.work / L"journal.json"), "Transaction already exists; use recovery");
    NotCancelled(j);
    auto files = Manifest(j.stage / L"app-files.json", j.version);
    VerifyStage(j, files);
    Dependencies(j);
    SpaceAndPermissions(j, files);
    NotCancelled(j);
    Status(j.work, "readyToExit");
    if (!ApprovedExit(j, old))
    {
        Status(j.work, "error", "Application did not approve shutdown and exit within 30 seconds");
        return 1;
    }
    Journal t;
    t.phase = "backingUp";
    try
    {
        Require(Running(j).empty(), "Another process from this installation is running");
        NotCancelled(j);
        Status(j.work, "backingUp");
        VerifyStage(j, files);
        t = Backup(j, files);
        NotCancelled(j);
        UnlockedTargets(j, t);
        Status(j.work, "installing");
        Apply(j, files, t);
        Status(j.work, "starting");
        if (fs::exists(j.work / L"health.json"))
            Require(DeleteFileW((j.work / L"health.json").c_str()), "Cannot clear stale health state");
        auto next = Launch(j, true, &t.launched);
        t.phase = "starting";
        SaveJournal(j, t);
        if (!Healthy(j, next.h))
            throw Failure("New application did not become healthy");
        t.phase = "complete";
        SaveJournal(j, t);
        Status(j.work, "complete");
        RotateOlderBackups(j);
        return 0;
    }
    catch (const std::exception &failure)
    {
        if (t.phase == "backingUp" || t.phase == "prepared")
        {
            try
            {
                FinishIntactTransaction(j);
                Status(j.work, "error", failure.what());
                if (Running(j).empty())
                    RestartOld(j, lock);
                return 1;
            }
            catch (const std::exception &)
            {
                Status(j.work, "recoveryRequired", "Installation remains intact; automatic restart could not finish");
                return 2;
            }
        }
        try
        {
            if (!StopNew(j, t))
            {
                Status(j.work, "recoveryRequired", "New application did not close; backup retained");
                return 2;
            }
            Rollback(j, t);
            RestartOld(j, lock);
            return 1;
        }
        catch (const std::exception &)
        {
            Status(j.work, "recoveryRequired", "Rollback could not finish safely; backup and journal retained");
            return 2;
        }
    }
}
} // namespace
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int count = 0;
    auto argv = CommandLineToArgvW(GetCommandLineW(), &count);
    fs::path work;
    try
    {
        Require(argv && count == 3 && (wcscmp(argv[1], L"--job") == 0 || wcscmp(argv[1], L"--recover") == 0),
                "Usage: TaskBarWidgets.Updater.exe --job|--recover job.json");
        auto path = Absolute(Utf8(argv[2]));
        auto j = LoadJob(path);
        work = j.work;
        bool recover = wcscmp(argv[1], L"--recover") == 0;
        LocalFree(argv);
        argv = nullptr;
        return Run(j, recover);
    }
    catch (const std::exception &e)
    {
        if (argv)
            LocalFree(argv);
        if (!work.empty())
            try
            {
                Status(work, "error", e.what());
            }
            catch (...)
            {
            }
        return 1;
    }
}
