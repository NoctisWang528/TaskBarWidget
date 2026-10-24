#include "CardPch.h"
#include "CardProtocol.h"
#include "CardText.h"

#include <winrt/Windows.Data.Json.h>

using namespace winrt::Windows::Data::Json;

namespace Cards::Protocol
{
    namespace
    {
        std::wstring Text(const JsonObject& object, const wchar_t* key, const std::wstring& fallback = L"")
        {
            if (!object) return fallback;
            if (!object.HasKey(key)) return fallback;
            try { return object.GetNamedString(key).c_str(); }
            catch (...) { return fallback; }
        }

        double Number(const JsonObject& object, const wchar_t* key, double fallback = 0)
        {
            if (!object || !object.HasKey(key)) return fallback;
            try
            {
                const double value = object.GetNamedNumber(key);
                return std::isfinite(value) ? value : fallback;
            }
            catch (...) { return fallback; }
        }

        int Integer(const JsonObject& object, const wchar_t* key, int fallback = 0)
        {
            const double value = Number(object, key, static_cast<double>(fallback));
            return static_cast<int>(value);
        }

        bool Boolean(const JsonObject& object, const wchar_t* key, bool fallback = false)
        {
            if (!object || !object.HasKey(key)) return fallback;
            try { return object.GetNamedBoolean(key); }
            catch (...) { return fallback; }
        }

        JsonObject Object(const JsonObject& parent, const wchar_t* key)
        {
            if (!parent || !parent.HasKey(key)) return nullptr;
            try
            {
                const auto value = parent.GetNamedValue(key);
                return value.ValueType() == JsonValueType::Object ? value.GetObject() : nullptr;
            }
            catch (...) { return nullptr; }
        }

        JsonArray Array(const JsonObject& parent, const wchar_t* key)
        {
            if (!parent || !parent.HasKey(key)) return nullptr;
            try
            {
                const auto value = parent.GetNamedValue(key);
                return value.ValueType() == JsonValueType::Array ? value.GetArray() : nullptr;
            }
            catch (...) { return nullptr; }
        }

        std::vector<std::wstring> Strings(const JsonObject& parent, const wchar_t* key)
        {
            std::vector<std::wstring> result;
            const auto array = Array(parent, key);
            if (!array) return result;
            for (const auto& entry : array)
            {
                if (entry.ValueType() != JsonValueType::String) continue;
                try { result.push_back(entry.GetString().c_str()); }
                catch (...) { }
            }
            return result;
        }

        bool MicaTuningFrom(const JsonObject& parent, MicaTuning& tuning)
        {
            const auto mica = Object(parent, L"mica");
            if (!mica) return false;
            tuning.vivid = Boolean(mica, L"vivid", tuning.vivid);
            tuning.tint = Number(mica, L"tintOpacity", tuning.tint);
            tuning.luminosity = Number(mica, L"luminosityOpacity", tuning.luminosity);
            tuning.darkTint = Number(mica, L"darkTintOpacity", tuning.darkTint);
            tuning.darkLuminosity = Number(mica, L"darkLuminosityOpacity", tuning.darkLuminosity);
            const auto clamp = [](double value) { return std::isfinite(value) ? std::clamp(value, 0.0, 1.0) : 0.0; };
            tuning.tint = clamp(tuning.tint);
            tuning.luminosity = clamp(tuning.luminosity);
            tuning.darkTint = clamp(tuning.darkTint);
            tuning.darkLuminosity = clamp(tuning.darkLuminosity);
            return true;
        }

        bool ConfigFrom(const JsonObject& quickCards, Config& config)
        {
            if (!quickCards) return false;
            const auto order = Strings(quickCards, L"order");
            if (!order.empty()) config.order = order;
            if (const auto columns = Object(quickCards, L"columns"))
            {
                for (const wchar_t* id : { L"weather", L"traffic", L"device", L"media" })
                    config.columns[id] = Integer(columns, id, config.columns[id]);
            }
            if (const auto enabled = Object(quickCards, L"enabled"))
            {
                for (const wchar_t* id : { L"weather", L"traffic", L"device", L"media" })
                    config.enabled[id] = Boolean(enabled, id, config.enabled[id]);
            }
            const auto mode = Text(quickCards, L"resourceMode");
            if (!mode.empty()) config.resourceMode = mode;
            const auto font = Text(quickCards, L"font");
            if (!font.empty()) config.font = font;
            MicaTuningFrom(quickCards, config.mica);
            config.revision += 1;
            return true;
        }

        bool MediaFrom(const JsonObject& media, MediaState& state)
        {
            if (!media) return false;
            const long long revision = static_cast<long long>(Number(media, L"revision", -1));
            if (revision >= 0 && revision < state.revision) return false;
            state.revision = revision;
            state.title = Text(media, L"title");
            state.artist = Text(media, L"artist");
            state.source = Text(media, L"source");
            state.position = Number(media, L"positionSeconds");
            state.duration = Number(media, L"durationSeconds");
            state.isPlaying = Boolean(media, L"isPlaying");
            state.canPrevious = Boolean(media, L"canPrevious");
            state.canNext = Boolean(media, L"canNext");
            state.canPlayPause = Boolean(media, L"canPlayPause");
            state.canSeek = Boolean(media, L"canSeek");
            state.selectedSourceId = Text(media, L"selectedSourceId");
            const auto cover = Text(media, L"coverDataUrl");
            if (cover != state.coverDataUrl)
            {
                state.coverDataUrl = cover;
                state.coverKey = std::to_wstring(state.revision) + L"|" + std::to_wstring(cover.size());
            }
            state.sources.clear();
            if (const auto sources = Array(media, L"sources"))
            {
                for (const auto& entry : sources)
                {
                    if (entry.ValueType() != JsonValueType::Object) continue;
                    const auto item = entry.GetObject();
                    MediaSource source;
                    source.id = Text(item, L"id");
                    source.label = Text(item, L"label");
                    source.iconDataUrl = Text(item, L"iconDataUrl");
                    if (!source.id.empty()) state.sources.push_back(std::move(source));
                }
            }
            return true;
        }

        bool AudioFrom(const JsonObject& audio, AudioState& state)
        {
            if (!audio) return false;
            const auto read = [](const JsonArray& array, std::vector<AudioEndpoint>& target)
            {
                target.clear();
                if (!array) return;
                for (const auto& entry : array)
                {
                    if (entry.ValueType() != JsonValueType::Object) continue;
                    const auto item = entry.GetObject();
                    AudioEndpoint endpoint;
                    endpoint.id = Text(item, L"id");
                    endpoint.name = Text(item, L"name");
                    endpoint.isDefault = Boolean(item, L"isDefault");
                    endpoint.isCommunications = Boolean(item, L"isCommunications");
                    endpoint.isMuted = Boolean(item, L"isMuted");
                    endpoint.canControl = Boolean(item, L"canControl", true);
                    endpoint.volume = std::clamp(Number(item, L"volume"), 0.0, 1.0);
                    if (!endpoint.id.empty()) target.push_back(std::move(endpoint));
                }
            };
            read(Array(audio, L"outputs"), state.outputs);
            read(Array(audio, L"inputs"), state.inputs);
            state.message = Text(audio, L"message");
            const long long revision = static_cast<long long>(Number(audio, L"revision", -1));
            if (revision >= 0) state.revision = revision;
            return true;
        }

        bool LyricsFrom(const JsonObject& lyrics, LyricsState& state)
        {
            if (!lyrics) return false;
            const long long revision = static_cast<long long>(Number(lyrics, L"revision", -1));
            if (revision >= 0 && revision < state.revision) return false;
            state.revision = revision;
            state.trackId = Text(lyrics, L"trackId");
            state.available = Boolean(lyrics, L"available");
            state.isPlaying = Boolean(lyrics, L"isPlaying");
            state.animateTransition = Boolean(lyrics, L"animateTransition", true);
            state.currentLineIndex = Integer(lyrics, L"currentLineIndex", -1);
            state.progress = std::clamp(Number(lyrics, L"progress"), 0.0, 1.0);
            state.wordScan = Number(lyrics, L"wordScanProgress", -1);
            state.scene = Text(lyrics, L"scene");
            state.current = Text(lyrics, L"current");
            state.next = Text(lyrics, L"next");
            state.currentTranslation = Text(lyrics, L"currentTranslation");
            state.nextTranslation = Text(lyrics, L"nextTranslation");
            state.isPureMusic = Boolean(lyrics, L"isPureMusic");
            if (const auto style = Object(lyrics, L"style"))
            {
                const auto font = Text(style, L"fontFamily");
                if (!font.empty()) state.fontFamily = font;
                state.fontSize = Number(style, L"fontSize", state.fontSize);
                state.fontWeight = Integer(style, L"fontWeight", state.fontWeight);
                const auto primary = Text(style, L"primaryColor");
                if (!primary.empty()) state.primaryColor = primary;
                const auto secondary = Text(style, L"secondaryColor");
                if (!secondary.empty()) state.secondaryColor = secondary;
                const auto translation = Text(style, L"translationColor");
                if (!translation.empty()) state.translationColor = translation;
                const auto scan = Text(style, L"wordScanOverlayColor");
                if (!scan.empty()) state.scanColor = scan;
                state.textShadow = Text(style, L"textShadow");
                state.textAlignment = Text(style, L"textAlignment");
                state.layoutScalePercent = Number(style, L"layoutScalePercent", state.layoutScalePercent);
            }
            return true;
        }

        bool TrafficFrom(const JsonObject& traffic, TrafficState& state)
        {
            if (!traffic) return false;
            const long long revision = static_cast<long long>(Number(traffic, L"revision", -1));
            if (revision >= 0 && revision < state.revision) return false;
            state.revision = revision;
            state.today = Text(traffic, L"today");
            state.scopeLabel = Text(traffic, L"scopeLabel");
            state.days.clear();
            state.totalKiB = 0;
            state.peakKiB = 0;
            if (const auto days = Array(traffic, L"days"))
            {
                for (const auto& entry : days)
                {
                    if (entry.ValueType() != JsonValueType::Object) continue;
                    const auto item = entry.GetObject();
                    TrafficDay day;
                    day.week = Integer(item, L"week");
                    day.weekday = Integer(item, L"weekday");
                    day.date = Text(item, L"date");
                    day.isFuture = Boolean(item, L"isFuture");
                    day.known = Boolean(item, L"known");
                    day.isToday = Boolean(item, L"isToday");
                    day.totalKiB = Number(item, L"totalKiB");
                    day.level = Integer(item, L"level", -1);
                    state.days.push_back(std::move(day));
                }
            }
            state.totalKiB = Number(traffic, L"totalKiB");
            state.peakKiB = Number(traffic, L"peakKiB");
            state.available = !state.days.empty();
            return true;
        }

        bool WeatherFrom(const JsonObject& weather, WeatherState& state)
        {
            if (!weather) return false;
            const long long revision = static_cast<long long>(Number(weather, L"revision", -1));
            if (revision >= 0 && revision < state.revision) return false;
            state.revision = revision;
            state.provider = Text(weather, L"provider");
            state.city = Text(weather, L"city");
            state.region = Text(weather, L"region");
            state.state = Text(weather, L"state");
            state.message = Text(weather, L"message");
            state.stale = Boolean(weather, L"stale");
            state.updating = Boolean(weather, L"updating");
            state.hasData = Boolean(weather, L"hasData");
            if (const auto details = Object(weather, L"details"))
            {
                if (const auto current = Object(details, L"current"))
                {
                    state.temperature = Text(current, L"temperature");
                    state.feelsLike = Text(current, L"feelsLike");
                    state.weather = Text(current, L"weather");
                    state.conditionKind = Text(current, L"conditionKind");
                    state.humidity = Text(current, L"humidity");
                    state.wind = Text(current, L"windDirection");
                    state.observedAt = Text(current, L"observedAt");
                }
                if (const auto air = Object(details, L"airQuality"))
                {
                    state.airQuality = Text(air, L"aqi");
                }
                state.forecast.clear();
                if (const auto forecast = Array(details, L"forecast"))
                {
                    for (const auto& entry : forecast)
                    {
                        if (entry.ValueType() != JsonValueType::Object) continue;
                        const auto item = entry.GetObject();
                        WeatherForecastDay day;
                        day.min = Text(item, L"min");
                        day.max = Text(item, L"max");
                        day.dayKind = Text(item, L"conditionDayKind");
                        day.nightKind = Text(item, L"conditionNightKind");
                        day.weatherDay = Text(item, L"weatherDay");
                        day.weatherNight = Text(item, L"weatherNight");
                        state.forecast.push_back(std::move(day));
                    }
                }
                state.alerts.clear();
                if (const auto alerts = Array(details, L"alerts"))
                {
                    for (const auto& entry : alerts)
                    {
                        if (entry.ValueType() != JsonValueType::Object) continue;
                        const auto item = entry.GetObject();
                        WeatherAlert alert;
                        alert.typeName = Text(item, L"typeName");
                        alert.level = Text(item, L"level");
                        alert.title = Text(item, L"title");
                        alert.text = Text(item, L"text");
                        alert.severity = Text(item, L"severity");
                        state.alerts.push_back(std::move(alert));
                    }
                }
            }
            return true;
        }

        bool UpdateFrom(const JsonObject& update, UpdateState& state)
        {
            if (!update) return false;
            const long long revision = static_cast<long long>(Number(update, L"revision", -1));
            if (revision >= 0 && revision < state.revision) return false;
            state.revision = revision;
            state.phase = Text(update, L"phase");
            state.currentVersion = Text(update, L"currentVersion");
            state.latestVersion = Text(update, L"latestVersion");
            state.message = Text(update, L"message");
            state.error = Text(update, L"error");
            state.bytesDownloaded = static_cast<std::int64_t>(Number(update, L"bytesDownloaded"));
            state.totalBytes = static_cast<std::int64_t>(Number(update, L"totalBytes"));
            state.canDownload = Boolean(update, L"canDownload");
            state.canInstall = Boolean(update, L"canInstall");
            return true;
        }
    }

    std::wstring JsonString(const std::wstring& value)
    {
        std::wstring escaped;
        escaped.reserve(value.size() + 8);
        for (const wchar_t character : value)
        {
            switch (character)
            {
            case L'"': escaped += L"\\\""; break;
            case L'\\': escaped += L"\\\\"; break;
            case L'\n': escaped += L"\\n"; break;
            case L'\r': escaped += L"\\r"; break;
            case L'\t': escaped += L"\\t"; break;
            default:
                if (character < 0x20) { wchar_t buffer[8]{}; swprintf_s(buffer, L"\\u%04x", character); escaped += buffer; }
                else escaped += character;
                break;
            }
        }
        return L"\"" + escaped + L"\"";
    }

    Message Parse(const std::string& utf8)
    {
        Message message;
        message.raw = Utf8ToWide(utf8);
        try
        {
            const auto object = JsonObject::Parse(message.raw);
            message.action = Text(object, L"action");
            if (const auto type = Text(object, L"type"); type != L"command") return message;
            if (message.action == L"cardSnapshot")
                message.kind = Kind::Snapshot;
            else if (message.action == L"cardReply")
            {
                message.kind = Kind::Reply;
                message.requestId = Text(object, L"requestId");
                message.success = Boolean(object, L"success");
                message.error = Text(object, L"error");
            }
            else if (message.action == L"cardReady" || message.action == L"shutdown")
                message.kind = Kind::Event;
            else
                return message;
            if (const auto payload = Object(object, L"payload"))
            {
                message.payloadJson = std::wstring(payload.Stringify().c_str());
                message.hasPayload = true;
            }
        }
        catch (...) { message.kind = Kind::Unknown; }
        return message;
    }

    bool ApplySnapshot(Snapshot& target, const std::wstring& payloadJson, Changes& changes)
    {
        try
        {
            const auto payload = JsonObject::Parse(payloadJson);
            if (!payload) return false;
            if (const auto quickCards = Object(payload, L"quickCards"))
                changes.config = ConfigFrom(quickCards, target.config);
            if (const auto media = Object(payload, L"media"))
                changes.media = MediaFrom(media, target.media);
            if (const auto audio = Object(payload, L"audio"))
                changes.audio = AudioFrom(audio, target.audio);
            if (const auto lyrics = Object(payload, L"lyrics"))
                changes.lyrics = LyricsFrom(lyrics, target.lyrics);
            if (const auto traffic = Object(payload, L"traffic"))
                changes.traffic = TrafficFrom(traffic, target.traffic);
            if (const auto weather = Object(payload, L"weather"))
                changes.weather = WeatherFrom(weather, target.weather);
            if (const auto update = Object(payload, L"update"))
                changes.update = UpdateFrom(update, target.update);
            const auto status = Text(payload, L"statusText");
            if (status != target.statusText) { target.statusText = status; changes.status = true; }
            target.connected = Boolean(payload, L"connected", true);
            return true;
        }
        catch (...) { return false; }
    }

    Reply ParseReply(const Message& message)
    {
        Reply reply;
        reply.requestId = message.requestId;
        reply.success = message.success;
        reply.error = message.error;
        reply.payloadJson = message.payloadJson;
        return reply;
    }

    bool ApplyAudioReply(AudioState& target, const std::wstring& payloadJson)
    {
        try { return AudioFrom(JsonObject::Parse(payloadJson), target); }
        catch (...) { return false; }
    }

    bool ApplyTrafficReply(TrafficState& target, const std::wstring& payloadJson)
    {
        try { return TrafficFrom(JsonObject::Parse(payloadJson), target); }
        catch (...) { return false; }
    }

    bool ApplyWeatherReply(WeatherState& target, const std::wstring& payloadJson)
    {
        try
        {
            const auto payload = JsonObject::Parse(payloadJson);
            if (!payload) return false;
            return WeatherFrom(payload, target);
        }
        catch (...) { return false; }
    }

    bool ApplyMediaReply(MediaState& target, const std::wstring& payloadJson)
    {
        try { return MediaFrom(JsonObject::Parse(payloadJson), target); }
        catch (...) { return false; }
    }

    std::wstring JsonAudioRequest(const wchar_t* flow, const wchar_t* deviceId, double volume, int muted)
    {
        std::wstring json = L"\"flow\":";
        json += JsonString(flow ? flow : L"output");
        if (deviceId) { json += L",\"deviceId\":"; json += JsonString(deviceId); }
        if (volume >= 0) { wchar_t buffer[32]{}; swprintf_s(buffer, L"%.6f", volume); json += L",\"volume\":"; json += buffer; }
        if (muted >= 0) json += muted ? L",\"muted\":true" : L",\"muted\":false";
        return json;
    }
}
