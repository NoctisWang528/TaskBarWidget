#include "pch.h"
#include "Common.h"
#include <atomic>
#include <winhttp.h>
#pragma comment(lib, "winhttp.lib")
//    //用于支持使用网络相关的类

std::wstring WeatherCCommon::StrToUnicode(const char* str, bool utf8)
{
    if (str == nullptr)
        return std::wstring();
    std::wstring result;
    int size;
    size = MultiByteToWideChar((utf8 ? CP_UTF8 : CP_ACP), 0, str, -1, NULL, 0);
    if (size <= 0) return std::wstring();
    wchar_t* str_unicode = new wchar_t[size + 1];
    MultiByteToWideChar((utf8 ? CP_UTF8 : CP_ACP), 0, str, -1, str_unicode, size);
    result.assign(str_unicode);
    delete[] str_unicode;
    return result;
}

std::string WeatherCCommon::UnicodeToStr(const wchar_t* wstr, bool utf8)
{
    if (wstr == nullptr)
        return std::string();
    std::string result;
    int size{ 0 };
    size = WideCharToMultiByte((utf8 ? CP_UTF8 : CP_ACP), 0, wstr, -1, NULL, 0, NULL, NULL);
    if (size <= 0) return std::string();
    char* str = new char[size + 1];
    WideCharToMultiByte((utf8 ? CP_UTF8 : CP_ACP), 0, wstr, -1, str, size, NULL, NULL);
    result.assign(str);
    delete[] str;
    return result;
}

std::wstring WeatherCCommon::URLEncode(const std::wstring& wstr)
{
    std::string str_utf8;
    std::wstring result{};
    wchar_t buff[4];
    str_utf8 = WeatherCCommon::UnicodeToStr(wstr.c_str(), true);
    for (const auto& ch : str_utf8)
    {
        if (ch == ' ')
            result.push_back(L'+');
        else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            result.push_back(static_cast<wchar_t>(ch));
        else if (ch == '-' || ch == '_' || ch == '.' || ch == '!' || ch == '~' || ch == '*'/* || ch == '\''*/ || ch == '(' || ch == ')')
            result.push_back(static_cast<wchar_t>(ch));
        else
        {
            swprintf_s(buff, L"%%%x", static_cast<unsigned char>(ch));
            result += buff;
        }
    }
    return result;
}

static std::atomic<bool> weather_stopping{false};
void WeatherCCommon::StopRequests() { weather_stopping = true; }

bool WeatherCCommon::GetUrl(const wchar_t* url, const wchar_t* headers, std::wstring& content)
{
    content.clear();
    if (weather_stopping) return false;
    URL_COMPONENTS parts{sizeof(parts)};
    parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(url, 0, 0, &parts)) return false;
    HINTERNET session = WinHttpOpen(L"TaskBarWidgets/1", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, nullptr, nullptr, 0);
    if (!session) return false;
    WinHttpSetTimeouts(session, 3000, 3000, 3000, 3000);
    std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
    path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
    HINTERNET connection = WinHttpConnect(session, host.c_str(), parts.nPort, 0);
    HINTERNET request = connection ? WinHttpOpenRequest(connection, L"GET", path.c_str(), nullptr,
        WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0) : nullptr;
    bool good = false;
    std::string bytes;
    if (request)
    {
        DWORD decompression = WINHTTP_DECOMPRESSION_FLAG_GZIP | WINHTTP_DECOMPRESSION_FLAG_DEFLATE;
        WinHttpSetOption(request, WINHTTP_OPTION_DECOMPRESSION, &decompression, sizeof(decompression));
        if (WinHttpSendRequest(request, headers, headers && *headers ? static_cast<DWORD>(-1) : 0, nullptr, 0, 0, 0)
            && WinHttpReceiveResponse(request, nullptr))
        {
            DWORD status{}, size = sizeof(status);
            good = WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, nullptr) && status == 200;
            while (good && !weather_stopping)
            {
                char buffer[8192]; DWORD received{};
                if (!WinHttpReadData(request, buffer, sizeof(buffer), &received)) { good = false; break; }
                if (!received) break;
                if (bytes.size() + received > 1024 * 1024) { good = false; break; }
                bytes.append(buffer, received);
            }
        }
        WinHttpCloseHandle(request);
    }
    if (connection) WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    good = good && !weather_stopping;
    if (good) content = StrToUnicode(bytes.c_str(), true);
    return good;
}
