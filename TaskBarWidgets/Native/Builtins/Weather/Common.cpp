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
            swprintf_s(buff, L"%%%02X", static_cast<unsigned char>(ch));
            result += buff;
        }
    }
    return result;
}

static std::atomic<bool> weather_stopping{false};
void WeatherCCommon::StopRequests() { weather_stopping = true; }

static thread_local DWORD weather_http_status{}, weather_transport_error{};
using RequestStage = WeatherCCommon::RequestStage;
static thread_local RequestStage weather_request_stage{};
unsigned long WeatherCCommon::LastHttpStatus() { return weather_http_status; }
unsigned long WeatherCCommon::LastTransportError() { return weather_transport_error; }
RequestStage WeatherCCommon::LastRequestStage() { return weather_request_stage; }
std::wstring WeatherCCommon::LastTransportErrorMessage()
{
    if (!weather_transport_error) return {};
    const wchar_t* stages[] = { L"无", L"解析地址", L"创建会话", L"设置选项", L"建立连接",
        L"创建请求", L"发送请求", L"接收响应", L"读取响应头", L"读取响应体" };
    const wchar_t* reason = L"网络传输失败";
    switch (weather_transport_error)
    {
    case ERROR_WINHTTP_NAME_NOT_RESOLVED: reason = L"DNS 解析失败"; break;
    case ERROR_WINHTTP_CANNOT_CONNECT: reason = L"连接失败"; break;
    case ERROR_WINHTTP_TIMEOUT: case ERROR_TIMEOUT: reason = L"请求超时"; break;
    case ERROR_WINHTTP_SECURE_FAILURE: case ERROR_WINHTTP_SECURE_CERT_DATE_INVALID:
    case ERROR_WINHTTP_SECURE_CERT_CN_INVALID: case ERROR_WINHTTP_SECURE_INVALID_CA:
    case ERROR_WINHTTP_SECURE_CERT_REV_FAILED: case ERROR_WINHTTP_SECURE_CHANNEL_ERROR:
    case ERROR_WINHTTP_SECURE_CERT_REVOKED: case ERROR_WINHTTP_SECURE_INVALID_CERT:
        reason = L"TLS 安全连接或证书验证失败"; break;
    default:
        if (weather_request_stage == RequestStage::Read) reason = L"响应体读取失败";
        break;
    }
    const unsigned stage = static_cast<unsigned>(weather_request_stage);
    return std::wstring(reason) + L"（阶段：" + stages[stage < _countof(stages) ? stage : 0] + L"）。";
}
#ifdef TBW_WEATHER_TESTS
static WeatherCCommon::TestTransport test_transport{};
void WeatherCCommon::SetTestTransport(TestTransport transport) { test_transport = transport; }
void WeatherCCommon::SetTestResponse(unsigned long httpStatus, unsigned long transportError, RequestStage stage)
{
    weather_http_status = httpStatus;
    weather_transport_error = transportError;
    weather_request_stage = stage;
}
#endif
bool WeatherCCommon::GetUrl(const wchar_t* url, const wchar_t* headers, std::wstring& content, bool allow_redirects)
{
    content.clear(); weather_http_status = weather_transport_error = 0;
    weather_request_stage = RequestStage::None;
#ifdef TBW_WEATHER_TESTS
    if (test_transport) return test_transport(url, headers, content, allow_redirects);
#endif
    auto fail = [](RequestStage stage, DWORD error) {
        weather_request_stage = stage; weather_transport_error = error; return false;
    };
    if (weather_stopping) return fail(RequestStage::None, ERROR_OPERATION_ABORTED);
    URL_COMPONENTS parts{sizeof(parts)};
    parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = static_cast<DWORD>(-1);
    if (!url) return fail(RequestStage::Url, ERROR_INVALID_PARAMETER);
    if (!WinHttpCrackUrl(url, 0, 0, &parts)) return fail(RequestStage::Url, GetLastError());
    if (parts.nScheme != INTERNET_SCHEME_HTTP && parts.nScheme != INTERNET_SCHEME_HTTPS)
        return fail(RequestStage::Url, ERROR_WINHTTP_UNRECOGNIZED_SCHEME);
    HINTERNET session = WinHttpOpen(L"TaskBarWidgets/1", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, nullptr, nullptr, 0);
    if (!session) return fail(RequestStage::Session, GetLastError());
    std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
    path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
    HINTERNET connection{}, request{};
    std::string bytes;
    const bool complete = [&]() -> bool
    {
        if (!WinHttpSetTimeouts(session, 3000, 3000, 3000, 3000))
            return fail(RequestStage::Options, GetLastError());
        connection = WinHttpConnect(session, host.c_str(), parts.nPort, 0);
        if (!connection) return fail(RequestStage::Connect, GetLastError());
        request = WinHttpOpenRequest(connection, L"GET", path.empty() ? L"/" : path.c_str(), nullptr,
            WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0);
        if (!request) return fail(RequestStage::Request, GetLastError());
        DWORD responseTimeout = 3000;
        if (!WinHttpSetOption(request, WINHTTP_OPTION_RECEIVE_RESPONSE_TIMEOUT, &responseTimeout, sizeof(responseTimeout)))
            return fail(RequestStage::Options, GetLastError());
        if (!allow_redirects)
        {
            DWORD disabled = WINHTTP_DISABLE_REDIRECTS;
            if (!WinHttpSetOption(request, WINHTTP_OPTION_DISABLE_FEATURE, &disabled, sizeof(disabled)))
                return fail(RequestStage::Options, GetLastError());
        }
        DWORD decompression = WINHTTP_DECOMPRESSION_FLAG_GZIP | WINHTTP_DECOMPRESSION_FLAG_DEFLATE;
        if (!WinHttpSetOption(request, WINHTTP_OPTION_DECOMPRESSION, &decompression, sizeof(decompression)))
            return fail(RequestStage::Options, GetLastError());
        if (!WinHttpSendRequest(request, headers, headers && *headers ? static_cast<DWORD>(-1) : 0, nullptr, 0, 0, 0))
            return fail(RequestStage::Send, GetLastError());
        if (!WinHttpReceiveResponse(request, nullptr)) return fail(RequestStage::Receive, GetLastError());
        DWORD size = sizeof(weather_http_status);
        if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX, &weather_http_status, &size, nullptr))
            return fail(RequestStage::Headers, GetLastError());
        wchar_t encoding[64]{}, length[32]{};
        size = sizeof(encoding);
        bool encoded = WinHttpQueryHeaders(request, WINHTTP_QUERY_CONTENT_ENCODING,
            WINHTTP_HEADER_NAME_BY_INDEX, encoding, &size, nullptr) != FALSE;
        if (!encoded)
        {
            const DWORD error = GetLastError();
            if (error != ERROR_WINHTTP_HEADER_NOT_FOUND) return fail(RequestStage::Headers, error);
        }
        encoded = encoded && (_wcsicmp(encoding, L"gzip") == 0 || _wcsicmp(encoding, L"deflate") == 0);
        size = sizeof(length);
        bool hasLength = WinHttpQueryHeaders(request, WINHTTP_QUERY_CONTENT_LENGTH,
            WINHTTP_HEADER_NAME_BY_INDEX, length, &size, nullptr) != FALSE;
        if (!hasLength)
        {
            const DWORD error = GetLastError();
            if (error != ERROR_WINHTTP_HEADER_NOT_FOUND) return fail(RequestStage::Headers, error);
        }
        const ULONGLONG expected = hasLength ? _wcstoui64(length, nullptr, 10) : 0;
        const size_t limit = weather_http_status == 200 ? 1024 * 1024 : 8192;
        const auto started = GetTickCount64();
        for (;;)
        {
            if (weather_stopping) return fail(RequestStage::Read, ERROR_OPERATION_ABORTED);
            const auto elapsed = GetTickCount64() - started;
            if (elapsed >= 10000) return fail(RequestStage::Read, ERROR_TIMEOUT);
            const int remaining = static_cast<int>(10000 - elapsed);
            if (!WinHttpSetTimeouts(request, 3000, 3000, 3000, remaining < 3000 ? remaining : 3000))
                return fail(RequestStage::Options, GetLastError());
            char buffer[8192]; DWORD received{};
            DWORD wanted = static_cast<DWORD>((limit - bytes.size()) < sizeof(buffer) ? limit - bytes.size() + 1 : sizeof(buffer));
            if (!WinHttpReadData(request, buffer, wanted, &received)) return fail(RequestStage::Read, GetLastError());
            if (weather_stopping) return fail(RequestStage::Read, ERROR_OPERATION_ABORTED);
            if (GetTickCount64() - started >= 10000) return fail(RequestStage::Read, ERROR_TIMEOUT);
            if (!received)
            {
                if (!encoded && hasLength && bytes.size() != expected)
                    return fail(RequestStage::Read, ERROR_WINHTTP_CONNECTION_ERROR);
                return true;
            }
            const size_t available = limit - bytes.size();
            bytes.append(buffer, received > available ? available : received);
            if (received > available) return fail(RequestStage::Read, ERROR_INSUFFICIENT_BUFFER);
        }
    }();
    if (request) WinHttpCloseHandle(request);
    if (connection) WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    if (weather_http_status != 200 || complete) content = StrToUnicode(bytes.c_str(), true);
    return complete && weather_http_status == 200;
}
