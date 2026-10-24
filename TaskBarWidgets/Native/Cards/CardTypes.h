#pragma once
// Native quick-card data model shared by the card STA thread, the pipe client
// and the painters. Every structure here is an immutable-once-published value:
// the UI thread replaces a whole field and never mutates state another thread
// still holds.

#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace Cards
{
    enum class Kind
    {
        Weather = 0,
        Traffic = 1,
        Device = 2,
        Media = 3,
        Update = 4,
    };

    const wchar_t* IdOf(Kind kind);
    bool KindFromId(const std::wstring& id, Kind& kind);

    struct MicaTuning
    {
        bool vivid{ true };
        double tint{ 0.10 };
        double luminosity{ 0.85 };
        double darkTint{ 0.15 };
        double darkLuminosity{ 0.90 };
        double TintFor(bool dark) const { return dark ? darkTint : tint; }
        double LuminosityFor(bool dark) const { return dark ? darkLuminosity : luminosity; }
    };

    // Persisted quick-card configuration. Layout, visibility and the Mica
    // parameters all come from here; none of it is re-derived natively.
    struct Config
    {
        std::vector<std::wstring> order{ L"weather", L"traffic", L"device", L"media" };
        std::map<std::wstring, int> columns{ { L"weather", 0 }, { L"traffic", 1 }, { L"device", 0 }, { L"media", 1 } };
        std::map<std::wstring, bool> enabled{ { L"weather", false }, { L"traffic", true }, { L"device", true }, { L"media", true } };
        std::wstring resourceMode{ L"Resident" };
        std::wstring font{ L"Microsoft YaHei UI" };
        MicaTuning mica{};
        long long revision{ 0 };

        bool IsEnabled(Kind kind) const;
        int ColumnFor(Kind kind) const;
        bool OnDemand() const;
    };

    struct MediaSource
    {
        std::wstring id;
        std::wstring label;
        std::wstring iconDataUrl;
    };

    struct MediaState
    {
        std::wstring title;
        std::wstring artist;
        std::wstring source;
        std::wstring selectedSourceId;
        std::wstring coverDataUrl;
        std::wstring coverKey;
        double position{ 0 };
        double duration{ 0 };
        bool isPlaying{ false };
        bool canPrevious{ false };
        bool canNext{ false };
        bool canPlayPause{ false };
        bool canSeek{ false };
        std::vector<MediaSource> sources;
        long long revision{ -1 };
    };

    struct AudioEndpoint
    {
        std::wstring id;
        std::wstring name;
        bool isDefault{ false };
        bool isCommunications{ false };
        bool isMuted{ false };
        bool canControl{ true };
        double volume{ 0 };
    };

    struct AudioState
    {
        std::vector<AudioEndpoint> outputs;
        std::vector<AudioEndpoint> inputs;
        std::wstring message;
        long long revision{ -1 };
    };

    struct LyricLine
    {
        std::wstring text;
        std::wstring translation;
        double scan{ 0 };
    };

    // Structured card lyrics frame, renderVersion 2. The front-end only lays out
    // and animates what this carries; it never queries lyrics itself.
    struct LyricsState
    {
        long long revision{ -1 };
        std::wstring trackId;
        bool available{ false };
        bool isPlaying{ false };
        bool isPureMusic{ false };
        bool translationMode{ false };
        bool animateTransition{ true };
        int currentLineIndex{ -1 };
        double progress{ 0 };
        double wordScan{ -1 };
        std::wstring scene;
        std::wstring current;
        std::wstring next;
        std::wstring currentTranslation;
        std::wstring nextTranslation;
        std::wstring fontFamily;
        double fontSize{ 13 };
        int fontWeight{ 600 };
        bool fontItalic{ false };
        bool fontUnderline{ false };
        bool fontStrikeOut{ false };
        std::wstring primaryColor;
        std::wstring secondaryColor;
        std::wstring translationColor;
        std::wstring scanColor;
        std::wstring textShadow;
        std::wstring textAlignment;
        double layoutScalePercent{ 100 };
    };

    struct TrafficDay
    {
        int week{ 0 };
        int weekday{ 0 };
        std::wstring date;
        bool isFuture{ false };
        bool known{ false };
        bool isToday{ false };
        bool selected{ false };
        double totalKiB{ 0 };
        int level{ -1 };
    };

    struct TrafficState
    {
        std::wstring today;
        std::wstring scopeLabel;
        std::vector<TrafficDay> days;
        std::wstring detail;
        std::wstring selectedDate;
        double totalKiB{ 0 };
        double peakKiB{ 0 };
        long long revision{ -1 };
        bool available{ false };

        // The sticky selection falls back to today; future days are never shown.
        const TrafficDay* Selected() const;
        void Select(const std::wstring& date);
    };

    struct WeatherAlert
    {
        std::wstring type;
        std::wstring publishedAt;
        std::wstring typeName;
        std::wstring level;
        std::wstring title;
        std::wstring text;
        std::wstring severity;
    };

    struct WeatherForecastDay
    {
        std::wstring min;
        std::wstring max;
        std::wstring dayKind;
        std::wstring nightKind;
        std::wstring weatherDay;
        std::wstring weatherNight;
    };

    struct WeatherState
    {
        std::wstring provider;
        std::wstring city;
        std::wstring cityId;
        std::wstring region;
        std::wstring lastSuccessCity;
        std::wstring lastSuccessCityId;
        std::wstring lastAttempt;
        std::wstring lastSuccess;
        std::wstring state;
        std::wstring message;
        std::wstring temperature;
        std::wstring feelsLike;
        std::wstring weather;
        std::wstring conditionKind;
        std::wstring humidity;
        std::wstring wind;
        std::wstring windScale;
        std::wstring windSpeed;
        std::wstring observedAt;
        std::wstring airQuality;
        std::wstring airLevel;
        std::wstring airCategory;
        std::wstring airIndexCode;
        std::wstring airIndexName;
        std::wstring airPublishedAt;
        std::wstring airError;
        std::wstring airAttribution;
        std::wstring pm25;
        std::wstring pm10;
        std::wstring pm25Unit;
        std::wstring pm10Unit;
        std::wstring alertsError;
        std::wstring alertsAttribution;
        bool stale{ false };
        bool updating{ false };
        bool hasData{ false };
        std::vector<WeatherForecastDay> forecast;
        std::vector<WeatherAlert> alerts;
        long long revision{ -1 };
    };

    struct UpdateState
    {
        long long revision{ -1 };
        std::wstring phase;
        std::wstring currentVersion;
        std::wstring latestVersion;
        std::wstring message;
        std::wstring error;
        std::int64_t bytesDownloaded{ 0 };
        std::int64_t totalBytes{ 0 };
        bool canDownload{ false };
        bool canInstall{ false };
        bool Visible() const;
        int ProgressPercent() const;
    };

    // Whole replaceable snapshot: new lyrics can never evict audio state because
    // every part carries its own revision.
    struct Snapshot
    {
        Config config;
        MediaState media;
        AudioState audio;
        LyricsState lyrics;
        TrafficState traffic;
        WeatherState weather;
        UpdateState update;
        std::wstring statusText;
        int toolWindowTheme{ 0 }; // AppSettings: System=0, Light=1, Dark=2.
        bool connected{ false };
    };

    enum class RequestKind
    {
        GetAudio,
        SelectDevice,
        SetVolume,
        SetMute,
        MediaCommand,
        GetTrafficHistory,
        GetWeatherStatus,
        OpenUpdateConfirm,
        OpenSettings,
        SubscribeLyrics,
    };
}
