#pragma once
#include <Windows.h>
#include <dpapi.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "bcrypt.lib")

namespace WeatherCredentials
{
    inline bool Clear(const std::filesystem::path& path);

    inline std::wstring Base64Encode(const std::vector<unsigned char>& data)
    {
        static constexpr wchar_t alphabet[] =
            L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        std::wstring value;
        value.reserve((data.size() + 2) / 3 * 4);
        for (size_t index = 0; index < data.size(); index += 3)
        {
            const size_t remaining = data.size() - index;
            const unsigned a = data[index];
            const unsigned b = remaining > 1 ? data[index + 1] : 0;
            const unsigned c = remaining > 2 ? data[index + 2] : 0;
            value.push_back(alphabet[a >> 2]);
            value.push_back(alphabet[((a & 3) << 4) | (b >> 4)]);
            value.push_back(remaining > 1 ? alphabet[((b & 15) << 2) | (c >> 6)] : L'=');
            value.push_back(remaining > 2 ? alphabet[c & 63] : L'=');
        }
        return value;
    }

    inline bool Base64Decode(const std::wstring& value, std::vector<unsigned char>& data)
    {
        auto digit = [](wchar_t character) -> int
        {
            if (character >= L'A' && character <= L'Z') return character - L'A';
            if (character >= L'a' && character <= L'z') return character - L'a' + 26;
            if (character >= L'0' && character <= L'9') return character - L'0' + 52;
            if (character == L'+') return 62;
            if (character == L'/') return 63;
            return -1;
        };
        data.clear();
        unsigned accumulator{};
        int bits{};
        bool padding{};
        for (const wchar_t character : value)
        {
            if (character == L'\r' || character == L'\n' || character == L' ' || character == L'\t') continue;
            if (character == L'=')
            {
                padding = true;
                continue;
            }
            if (padding) return false;
            const int current = digit(character);
            if (current < 0) return false;
            accumulator = (accumulator << 6) | static_cast<unsigned>(current);
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                data.push_back(static_cast<unsigned char>((accumulator >> bits) & 0xff));
            }
        }
        return !data.empty() && bits < 6;
    }

    inline std::string JsonString(const std::wstring& value)
    {
        std::string utf8;
        if (!value.empty())
        {
            const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.c_str(),
                static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
            if (size <= 0) return {};
            utf8.resize(size);
            WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.c_str(),
                static_cast<int>(value.size()), utf8.data(), size, nullptr, nullptr);
        }
        std::string result = "\"";
        for (const unsigned char c : utf8)
        {
            if (c == L'\\' || c == L'"') { result += '\\'; result += static_cast<char>(c); }
            else if (c < 0x20) { char buffer[7]{}; sprintf_s(buffer, "\\u%04x", c); result += buffer; }
            else result += static_cast<char>(c);
        }
        return result + "\"";
    }

    inline std::wstring ExtractJsonString(const std::string& json, const std::string& field)
    {
        const auto begin = json.find("\"" + field + "\"");
        if (begin == std::string::npos) return {};
        const auto colon = json.find(':', begin + field.size() + 2);
        if (colon == std::string::npos) return {};
        auto start = colon + 1;
        while (start < json.size() && (json[start] == ' ' || json[start] == '\t' ||
            json[start] == '\r' || json[start] == '\n')) ++start;
        if (start >= json.size() || json[start] != '"') return {};
        ++start;
        std::string value;
        for (size_t index = start; index < json.size(); ++index)
        {
            const char c = json[index];
            if (c == L'"') break;
            if (c != L'\\')
            {
                value += c;
                continue;
            }
            if (++index >= json.size()) break;
            const char escape = json[index];
            if (escape == L'u' && index + 4 < json.size())
            {
                unsigned code{};
                bool valid = true;
                for (int digit = 0; digit < 4; ++digit)
                {
                    const char hex = json[index + 1 + digit];
                    code <<= 4;
                    if (hex >= L'0' && hex <= L'9') code += hex - L'0';
                    else if (hex >= L'a' && hex <= L'f') code += hex - L'a' + 10;
                    else if (hex >= L'A' && hex <= L'F') code += hex - L'A' + 10;
                    else { valid = false; break; }
                }
                if (!valid) break;
                index += 4;
                if (code <= 0x7f) value += static_cast<char>(code);
                else if (code <= 0x7ff)
                {
                    value += static_cast<char>(0xc0 | (code >> 6));
                    value += static_cast<char>(0x80 | (code & 0x3f));
                }
                else
                {
                    value += static_cast<char>(0xe0 | (code >> 12));
                    value += static_cast<char>(0x80 | ((code >> 6) & 0x3f));
                    value += static_cast<char>(0x80 | (code & 0x3f));
                }
                continue;
            }
            if (escape == L'b') value += '\b';
            else if (escape == L'f') value += '\f';
            else if (escape == L'n') value += '\n';
            else if (escape == L'r') value += '\r';
            else if (escape == L't') value += '\t';
            else value += escape;
        }
        if (value.empty()) return {};
        const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
            static_cast<int>(value.size()), nullptr, 0);
        if (size <= 0) return {};
        std::wstring result(size, L'\0');
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
            static_cast<int>(value.size()), result.data(), size);
        return result;
    }

    inline bool Read(const std::filesystem::path& path, std::wstring& apiKey)
    {
        apiKey.clear();
        std::ifstream input(path, std::ios::binary);
        if (!input) return false;
        std::string json((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
        const auto encoded = ExtractJsonString(json, "protectedData");
        std::vector<unsigned char> protectedData;
        if (!Base64Decode(encoded, protectedData) || protectedData.empty()) return false;
        static const std::string entropyText = "TaskBarWidgets/weather-appkey/v1";
        DATA_BLOB entropy{static_cast<DWORD>(entropyText.size()),
            reinterpret_cast<BYTE*>(const_cast<char*>(entropyText.data()))};
        DATA_BLOB cipher{static_cast<DWORD>(protectedData.size()), protectedData.data()};
        DATA_BLOB plain{};
        if (!CryptUnprotectData(&cipher, nullptr, &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &plain))
            return false;
        apiKey.assign(reinterpret_cast<wchar_t*>(plain.pbData), plain.cbData / sizeof(wchar_t));
        SecureZeroMemory(plain.pbData, plain.cbData);
        LocalFree(plain.pbData);
        return !apiKey.empty();
    }

    inline bool Write(const std::filesystem::path& path, const std::wstring& apiKey)
    {
        if (apiKey.empty()) return Clear(path);
        static const std::string entropyText = "TaskBarWidgets/weather-appkey/v1";
        DATA_BLOB entropy{static_cast<DWORD>(entropyText.size()),
            reinterpret_cast<BYTE*>(const_cast<char*>(entropyText.data()))};
        DATA_BLOB plain{static_cast<DWORD>(apiKey.size() * sizeof(wchar_t)),
            reinterpret_cast<BYTE*>(const_cast<wchar_t*>(apiKey.data()))};
        DATA_BLOB cipher{};
        if (!CryptProtectData(&plain, L"TaskBarWidgets weather credential", &entropy,
                nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &cipher))
            return false;
        std::vector<unsigned char> bytes(cipher.pbData, cipher.pbData + cipher.cbData);
        LocalFree(cipher.pbData);
        const std::wstring wideEncoded = Base64Encode(bytes);
        std::string encoded;
        encoded.reserve(wideEncoded.size());
        for (const wchar_t character : wideEncoded)
            encoded.push_back(static_cast<char>(character));
        const std::string json = "{\r\n  \"version\": 1,\r\n  \"protectedData\": \"" +
            encoded + "\"\r\n}\r\n";
        std::error_code error;
        std::filesystem::create_directories(path.parent_path(), error);
        const auto temporary = path.wstring() + L"." + std::to_wstring(GetCurrentProcessId()) + L"." +
            std::to_wstring(GetTickCount64()) + L".tmp";
        HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        DWORD written{};
        bool ok = WriteFile(file, json.data(), static_cast<DWORD>(json.size()), &written, nullptr) &&
            written == json.size() && FlushFileBuffers(file);
        CloseHandle(file);
        if (ok) ok = MoveFileExW(temporary.c_str(), path.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
        if (!ok) DeleteFileW(temporary.c_str());
        return ok;
    }

    inline bool Clear(const std::filesystem::path& path)
    {
        if (!std::filesystem::exists(path)) return true;
        return DeleteFileW(path.c_str()) != FALSE;
    }
}
