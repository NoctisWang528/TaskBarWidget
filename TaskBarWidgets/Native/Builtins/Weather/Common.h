#pragma once
#include <string>

class WeatherCCommon
{
public:
    //将const char*字符串转换成宽字符字符串
    static std::wstring StrToUnicode(const char* str, bool utf8 = false);

    static std::string UnicodeToStr(const wchar_t* wstr, bool utf8 = false);

    //将一个字符串转换成URL编码（以UTF8编码格式）
    static std::wstring URLEncode(const std::wstring& wstr);

    // 解码gzip压缩的字节数据
    static void StopRequests();
    static bool GetUrl(const wchar_t* url, const wchar_t* headers, std::wstring& content);
};
