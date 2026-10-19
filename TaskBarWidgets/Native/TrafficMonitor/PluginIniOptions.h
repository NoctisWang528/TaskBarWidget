#pragma once
#include <string>

// Edit only a known boolean token. Every other byte (BOM, comments, credentials,
// unknown settings and line endings) remains unchanged. Ambiguous keys are rejected.
namespace PluginIniOptions
{
    struct ValueToken { size_t start{}, length{}; std::string value; };
    struct BooleanToken
    {
        size_t start{}, length{};
        bool value{}, numeric{};
    };
    inline std::string Trim(std::string text)
    {
        const auto first = text.find_first_not_of(" \t\r");
        if (first == std::string::npos) return {};
        return text.substr(first, text.find_last_not_of(" \t\r") - first + 1);
    }
    inline std::string Lower(std::string text)
    {
        for (auto& c : text) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
        return text;
    }
    inline bool FindValue(const std::string& bytes, const std::string& section, const std::string& key, ValueToken& result, bool* missing = nullptr)
    {
        if (missing) *missing = false;
        if (bytes.find('\0') != std::string::npos) return false;
        size_t cursor = bytes.compare(0, 3, "\xef\xbb\xbf") == 0 ? 3 : 0;
        std::string current;
        int matches = 0;
        while (cursor < bytes.size())
        {
            const size_t newline = bytes.find('\n', cursor);
            const size_t end = newline == std::string::npos ? bytes.size() : newline;
            const auto line = bytes.substr(cursor, end - cursor);
            const auto trimmed = Trim(line);
            if (!trimmed.empty() && trimmed.front() == '[')
            {
                const auto close = trimmed.find(']');
                current = close == std::string::npos ? std::string{} : Lower(Trim(trimmed.substr(1, close - 1)));
            }
            else if (current == Lower(section) && !trimmed.empty() && trimmed.front() != ';' && trimmed.front() != '#')
            {
                const auto equals = line.find('=');
                if (equals != std::string::npos && Lower(Trim(line.substr(0, equals))) == Lower(key))
                {
                    if (++matches > 1) return false;
                    const auto value = Trim(line.substr(equals + 1));
                    const auto start = value.empty() ? equals + 1 : line.find_first_not_of(" \t", equals + 1);
                    result = {cursor + start, value.size(), value};
                }
            }
            cursor = newline == std::string::npos ? bytes.size() : newline + 1;
        }
        if (missing) *missing = matches == 0;
        return matches == 1;
    }
    inline bool SetValue(std::string& bytes, const std::string& section, const std::string& key, const std::string& value)
    {
        ValueToken token;
        if (value.find_first_of("\r\n\0", 0, 3) != std::string::npos || !FindValue(bytes, section, key, token)) return false;
        bytes.replace(token.start, token.length, value);
        return true;
    }
    // Remove one unambiguous key line while preserving BOM, comments and the
    // file's existing line-ending style. Missing keys are already migrated.
    inline bool DeleteValue(std::string& bytes, const std::string& section, const std::string& key)
    {
        if (bytes.find('\0') != std::string::npos) return false;
        size_t cursor = bytes.compare(0, 3, "\xef\xbb\xbf") == 0 ? 3 : 0;
        std::string current;
        int matches = 0; size_t matchStart{}, matchEnd{};
        while (cursor < bytes.size())
        {
            const size_t newline = bytes.find('\n', cursor);
            const size_t end = newline == std::string::npos ? bytes.size() : newline;
            const auto line = bytes.substr(cursor, end - cursor);
            const auto trimmed = Trim(line);
            if (!trimmed.empty() && trimmed.front() == '[')
            {
                const auto close = trimmed.find(']');
                current = close == std::string::npos ? std::string{} : Lower(Trim(trimmed.substr(1, close - 1)));
            }
            else if (current == Lower(section) && !trimmed.empty() && trimmed.front() != ';' && trimmed.front() != '#')
            {
                const auto equals = line.find('=');
                if (equals != std::string::npos && Lower(Trim(line.substr(0, equals))) == Lower(key))
                {
                    if (++matches > 1) return false;
                    matchStart = cursor; matchEnd = newline == std::string::npos ? bytes.size() : newline + 1;
                }
            }
            cursor = newline == std::string::npos ? bytes.size() : newline + 1;
        }
        if (matches != 1) return false;
        bytes.erase(matchStart, matchEnd - matchStart);
        return true;
    }
    // Add only an absent allowlisted key; ambiguous/unsupported files stay untouched.
    inline bool EnsureValue(std::string& bytes, const std::string& section, const std::string& key, const std::string& value)
    {
        ValueToken token; bool missing{};
        if (FindValue(bytes, section, key, token, &missing)) return true;
        if (!missing || value.find_first_of("\r\n\0", 0, 3) != std::string::npos) return false;
        const std::string newline = bytes.find("\r\n") == std::string::npos ? "\n" : "\r\n";
        size_t cursor = bytes.compare(0, 3, "\xef\xbb\xbf") == 0 ? 3 : 0;
        while (cursor < bytes.size())
        {
            const auto next = bytes.find('\n', cursor);
            const auto end = next == std::string::npos ? bytes.size() : next;
            const auto line = Trim(bytes.substr(cursor, end - cursor));
            const auto close = line.find(']');
            if (!line.empty() && line.front() == '[' && close != std::string::npos
                && Lower(Trim(line.substr(1, close - 1))) == Lower(section))
            {
                const auto insert = next == std::string::npos ? bytes.size() : next + 1;
                bytes.insert(insert, (next == std::string::npos ? newline : "") + key + "=" + value + newline);
                return true;
            }
            cursor = next == std::string::npos ? bytes.size() : next + 1;
        }
        bytes += (bytes.empty() || bytes.back() == '\n' ? "" : newline) + "[" + section + "]" + newline + key + "=" + value + newline;
        return true;
    }
    inline bool FindBoolean(const std::string& bytes, const std::string& section,
        const std::string& key, BooleanToken& result)
    {
        if (bytes.find('\0') != std::string::npos) return false; // UTF-16 needs the native editor.
        size_t cursor = bytes.compare(0, 3, "\xef\xbb\xbf") == 0 ? 3 : 0;
        std::string current;
        int matches = 0;
        while (cursor < bytes.size())
        {
            const size_t newline = bytes.find('\n', cursor);
            const size_t end = newline == std::string::npos ? bytes.size() : newline;
            const std::string line = bytes.substr(cursor, end - cursor);
            const std::string trimmed = Trim(line);
            if (!trimmed.empty() && trimmed.front() == '[')
            {
                const auto close = trimmed.find(']');
                const auto suffix = close == std::string::npos ? std::string{} : Trim(trimmed.substr(close + 1));
                current = close != std::string::npos && (suffix.empty() || suffix.front() == ';' || suffix.front() == '#')
                    ? Lower(Trim(trimmed.substr(1, close - 1))) : std::string{};
            }
            else if (current == Lower(section) && !trimmed.empty() && trimmed.front() != ';' && trimmed.front() != '#')
            {
                const size_t equals = line.find('=');
                if (equals != std::string::npos && Lower(Trim(line.substr(0, equals))) == Lower(key))
                {
                    if (++matches > 1) return false;
                    const size_t start = line.find_first_not_of(" \t", equals + 1);
                    if (start == std::string::npos) return false;
                    const size_t tokenEnd = line.find_first_of(" \t\r;#", start);
                    const size_t stop = tokenEnd == std::string::npos ? line.size() : tokenEnd;
                    const std::string token = line.substr(start, stop - start);
                    const std::string suffix = Trim(line.substr(stop));
                    if (!suffix.empty() && suffix.front() != ';' && suffix.front() != '#') return false;
                    if (token != "0" && token != "1" && token != "true" && token != "false") return false;
                    result = {cursor + start, stop - start, token == "1" || token == "true", token == "0" || token == "1"};
                }
            }
            cursor = newline == std::string::npos ? bytes.size() : newline + 1;
        }
        return matches == 1;
    }
    inline bool SetBoolean(std::string& bytes, const std::string& section, const std::string& key, bool value)
    {
        BooleanToken token;
        if (!FindBoolean(bytes, section, key, token)) return false;
        bytes.replace(token.start, token.length, token.numeric ? (value ? "1" : "0") : (value ? "true" : "false"));
        return true;
    }
}
