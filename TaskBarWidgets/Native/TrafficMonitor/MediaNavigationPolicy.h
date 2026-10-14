#pragma once
#include <string_view>

// NavigateToString has been observed to emit a data: NavigationStarting URI on
// the installed runtime, even though the document origin is about:blank/null.
class MediaNavigationPolicy
{
public:
    void ExpectHostDocument() { m_expected = true; }
    void Complete() { m_expected = false; }
    bool Allow(std::wstring_view uri, bool user_initiated, bool redirected)
    {
        if (user_initiated || redirected) return false;
        if (uri == L"about:blank")
        {
            m_expected = false;
            return true;
        }
        constexpr std::wstring_view html_prefix = L"data:text/html;charset=utf-8;base64,";
        if (m_expected && uri.starts_with(html_prefix) && uri.size() <= 8 * 1024 * 1024)
        {
            m_expected = false;
            return true;
        }
        return false;
    }
private:
    bool m_expected{};
};
