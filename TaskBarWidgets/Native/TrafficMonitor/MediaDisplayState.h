#pragma once
#include <string>
#include <cassert>

// Track, artwork, position and unrelated sessions deliberately do not clear a wheel choice.
struct MediaDisplayState
{
    std::wstring source;
    bool playing{};
    bool hasMedia{};
    bool connected{};
    int manual{}; // 0 automatic, 1 monitoring, 2 media
    int wheelRemainder{};

    void Frame(const std::wstring& nextSource, bool nextPlaying, bool nextHasMedia)
    {
        if (source != nextSource || playing != nextPlaying || hasMedia != nextHasMedia)
            manual = 0;
        source = nextSource;
        playing = nextPlaying;
        hasMedia = nextHasMedia;
        connected = true;
    }
    void Disconnect()
    {
        connected = false;
        source.clear();
        playing = hasMedia = false;
        manual = wheelRemainder = 0;
    }
    void Wheel(int delta)
    {
        wheelRemainder += delta;
        if (wheelRemainder >= 120)
        {
            manual = 2;
            wheelRemainder %= 120;
        }
        else if (wheelRemainder <= -120)
        {
            manual = 1;
            wheelRemainder %= 120;
        }
    }
    bool MediaVisible() const { return connected && hasMedia && (manual == 2 || (manual == 0 && playing)); }
};

inline void TestMediaDisplayState()
{
    MediaDisplayState state;
    state.Frame(L"QQMusic", true, true);
    assert(state.MediaVisible());
    state.Wheel(-40); state.Wheel(-40);
    assert(state.MediaVisible());
    state.Wheel(-40);
    assert(!state.MediaVisible());
    state.Frame(L"QQMusic", true, true); // next track, same playback/source
    assert(state.manual == 1 && !state.MediaVisible());
    state.Frame(L"QQMusic", false, true);
    assert(state.manual == 0 && !state.MediaVisible());
    state.Wheel(120);
    assert(state.MediaVisible());
    state.Frame(L"Chrome", true, true);
    assert(state.manual == 0 && state.MediaVisible());
    state.Disconnect();
    assert(!state.MediaVisible() && state.manual == 0);
    state.Wheel(120);
    assert(!state.MediaVisible());
    state.Frame(L"Chrome", true, true);
    assert(state.MediaVisible() && state.manual == 0);
}
