#include "../TrafficMonitor/MediaNavigationPolicy.h"
#include <cassert>
#include <string>

int main()
{
    // The URI form captured in native-media.log; the previous about:blank-only
    // check cancelled this host-generated document with WebErrorStatus 14.
    constexpr auto observed = L"data:text/html;charset=utf-8;base64,PCFkb2N0eXBlIGh0bWw+";
    MediaNavigationPolicy policy;
    assert(!policy.Allow(observed, false, false));
    policy.ExpectHostDocument();
    assert(policy.Allow(observed, false, false));
    assert(!policy.Allow(observed, false, false));
    policy.ExpectHostDocument();
    assert(!policy.Allow(observed, true, false));
    assert(!policy.Allow(observed, false, true));
    assert(policy.Allow(observed, false, false));
    policy.ExpectHostDocument();
    assert(!policy.Allow(L"https://example.com/", false, false));
    assert(!policy.Allow(L"file:///C:/Windows/file.html", false, false));
    assert(!policy.Allow(L"data:text/javascript,alert(1)", false, false));
    assert(!policy.Allow(L"about:blank.evil", false, false));
    assert(policy.Allow(L"about:blank", false, false));
    policy.Complete();
    assert(!policy.Allow(observed, false, false));
    policy.ExpectHostDocument();
    assert(!policy.Allow(std::wstring(observed) + std::wstring(8 * 1024 * 1024, L'A'), false, false));
}
