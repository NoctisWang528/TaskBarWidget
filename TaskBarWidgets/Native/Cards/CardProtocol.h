#pragma once
// Card channel protocol.
//
// Three message kinds only:
//   cardSnapshot  complete replaceable state (config, media, audio, lyrics,
//                 traffic, weather, update), each part carrying its revision
//   cardRequest   requestId + payload { action, ... }
//   cardReply     requestId + success/error/payload
//
// Parsing lives here so the painters only ever see typed values.

#include "CardTypes.h"

#include <string>
#include <vector>

namespace Cards::Protocol
{
    enum class Kind
    {
        Unknown,
        Snapshot,
        Reply,
        Event,
    };

    struct Message
    {
        Kind kind{ Kind::Unknown };
        std::wstring action;
        std::wstring requestId;
        std::wstring error;
        bool success{ false };
        bool hasPayload{ false };
        std::wstring payloadJson;
        std::wstring raw;
    };

    Message Parse(const std::string& utf8);

    // Applies the parts present in `snapshot.payload` onto `target` and reports
    // which parts changed. Older revisions are ignored per part.
    struct Changes
    {
        bool config{ false };
        bool media{ false };
        bool audio{ false };
        bool lyrics{ false };
        bool traffic{ false };
        bool weather{ false };
        bool update{ false };
        bool status{ false };
        bool Any() const
        {
            return config || media || audio || lyrics || traffic || weather || update || status;
        }
    };

    bool ApplySnapshot(Snapshot& target, const std::wstring& payloadJson, Changes& changes);

    struct Reply
    {
        std::wstring requestId;
        std::wstring action;
        bool success{ false };
        std::wstring error;
        std::wstring payloadJson;
    };

    Reply ParseReply(const Message& message);

    // Reply payloads for the actions the cards issue.
    bool ApplyAudioReply(AudioState& target, const std::wstring& payloadJson);
    bool ApplyTrafficReply(TrafficState& target, const std::wstring& payloadJson);
    bool ApplyWeatherReply(WeatherState& target, const std::wstring& payloadJson);
    bool ApplyMediaReply(MediaState& target, const std::wstring& payloadJson);

    // Builders for the requests the card UI issues.
    std::wstring JsonAudioRequest(const wchar_t* flow, const wchar_t* deviceId, double volume, int muted);
    std::wstring JsonString(const std::wstring& value);
}
