#include "../TrafficMonitor/PluginIniOptions.h"
#include "BuiltinIniOptionsTests.h"
#include <cassert>
#include <iostream>

int main()
{
    TestBuiltinIniOptions();
    using namespace PluginIniOptions;
    // Synthetic credentials only; the test never reads installed plugin files.
    std::string bytes = "\xef\xbb\xbf[config]\r\nshow_weather_icon = true ; retain comment\r\ncity_name = \xe5\x8f\xb0\xe5\x8c\x97\r\n[hfw]\r\nAppKey = synthetic-test-only\r\nshow_rt_wind = false\r\n";
    const auto original = bytes;
    BooleanToken token;
    assert(FindBoolean(bytes, "config", "show_weather_icon", token) && token.value && !token.numeric);
    assert(SetBoolean(bytes, "config", "show_weather_icon", false));
    auto expected = original; expected.replace(expected.find("true"), 4, "false");
    assert(bytes == expected); // BOM, UTF-8 city, credential, comments and CRLF preserved byte for byte.
    assert(SetBoolean(bytes, "hfw", "show_rt_wind", true));
    assert(bytes.find("AppKey = synthetic-test-only\r\n") != std::string::npos);
    std::string numeric = "[config]\nshow_percent=0\nunknown = 42";
    assert(SetBoolean(numeric, "config", "show_percent", true));
    assert(numeric == "[config]\nshow_percent=1\nunknown = 42");
    std::string duplicate = "[config]\nshow_percent=0\nshow_percent=1\n";
    assert(!SetBoolean(duplicate, "config", "show_percent", true));
    assert(duplicate == "[config]\nshow_percent=0\nshow_percent=1\n");
    std::string mixedCaseDuplicate = "[config]\nshow_percent=0\nSHOW_PERCENT=1\n";
    assert(!SetBoolean(mixedCaseDuplicate, "config", "show_percent", true));
    for (const auto* invalid : {"yes", "true extra", "true;comment\nshow_percent=0"})
    {
        std::string unsupported = std::string("[config]\nshow_percent=") + invalid;
        assert(!SetBoolean(unsupported, "config", "show_percent", true));
    }
    std::string unrelated = "[other]\nshow_percent=1\n";
    assert(!SetBoolean(unrelated, "config", "show_percent", true));
    unrelated = "[config]\n[other] ; section comment\nshow_percent=1\n";
    assert(!SetBoolean(unrelated, "config", "show_percent", true));
    unrelated = "[config]\n[malformed\nshow_percent=1\n";
    assert(!SetBoolean(unrelated, "config", "show_percent", true));
    std::string utf16("\xff\xfe[\0c\0o\0n\0f\0i\0g\0]\0", 18);
    assert(!FindBoolean(utf16, "config", "show_percent", token));
    std::cout << "Plugin INI allowlist token and byte-preservation tests passed.\n";
}
