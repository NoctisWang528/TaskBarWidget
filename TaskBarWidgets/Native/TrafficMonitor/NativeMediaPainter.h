#pragma once

#include "MediaAnimation.h"
#include "MediaFrameContract.h"
#include "MediaLayoutModel.h"

#include <d2d1_1.h>
#include <dwrite_3.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <cstdint>
#include <cstddef>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <tuple>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace NativeMedia
{
    enum class HitAction
    {
        None,
        Panel,
        Previous,
        PlayPause,
        Next
    };

    struct PaintState
    {
        EffectiveStyle style;
        Frame frame;
        const Cover* cover{};
        SpectrumTuning tuning;
    };

    struct LyricsPaint
    {
        const PaintState* state{};
        float width{};
        float height{};
        float dpiScale{ 1.0f };
        std::optional<double> wordScanProgress;
        double secondaryOpacity{ 0.72 };
        TransitionKind transitionKind{ TransitionKind::Immediate };
        const Frame* transitionFrom{};
        double transitionProgress{ 1.0 };
        bool reduceMotion{};
    };

    struct SpectrumPaint
    {
        const PaintState* state{};
        float width{};
        float height{};
        float dpiScale{ 1.0f };
        const std::vector<double>* bars{};
    };

    struct CoverPaint
    {
        const PaintState* state{};
        float width{};
        float height{};
        float dpiScale{ 1.0f };
        ID2D1Bitmap1* image{};
        const Cover* cover{};
    };

    struct BrowserPaint
    {
        const PaintState* state{};
        float width{};
        float height{};
        float dpiScale{ 1.0f };
    };

    struct CoverBitmap
    {
        Microsoft::WRL::ComPtr<ID2D1Bitmap1> bitmap;
        std::uint64_t key{};
        bool appIcon{};
    };

    class NativeMediaPainter
    {
    public:
        bool Initialize(ID2D1Factory1* factory, ID2D1Device* device,
            IDWriteFactory* writeFactory, std::wstring& error);
        void SetDevice(ID2D1Device* device);
        void ResetDeviceResources();

        void PaintBackground(ID2D1DeviceContext* context, const PaintState& state,
            float width, float height, float dpiScale);
        void PaintLyrics(ID2D1DeviceContext* context, const LyricsPaint& paint);
        void PaintSpectrum(ID2D1DeviceContext* context, const SpectrumPaint& paint);
        void PaintCover(ID2D1DeviceContext* context, const CoverPaint& paint);
        void PaintBrowser(ID2D1DeviceContext* context, const BrowserPaint& paint);

        bool PrepareCover(const Cover& cover, UINT coverEdgePx, CoverBitmap& result);
        bool HitTest(const BrowserPaint& paint, float x, float y, HitAction& action) const;

        void BeginPaintFrame();
        void EndPaintFrame();
        void ReleaseInactiveTextLayouts();
        void ReleaseCoverBitmapsExcept(const std::unordered_set<std::uint64_t>& keys);

        static D2D1_COLOR_F ParseColor(const std::wstring& value, D2D1_COLOR_F fallback);
        static std::uint64_t HashText(const std::wstring& value);
        // Single derivation for cover cache keys: PrepareCover and the renderer's
        // decode-failure fallback must agree on the key for a cover and decode edge.
        static std::uint64_t CoverCacheKey(const Cover& cover, UINT coverEdgePx);

        // Built-in footprint counters sampled by the native metrics line.
        std::size_t TextLayoutCount() const { return m_layouts.size(); }
        std::size_t TextFormatCount() const { return m_formats.size(); }
        std::size_t BrushCount() const { return m_brushes.size(); }
        std::size_t CoverCount() const { return m_covers.size(); }
        // Cached cover bitmaps are 32bpp PBGRA, so each pixel costs 4 bytes.
        std::uint64_t CoverCacheBytes() const;

    private:
        struct TextFormatKey
        {
            std::wstring family;
            float size{};
            int weight{};
            bool italic{};
            bool underline{};
            bool strikeOut{};
            int alignment{};

            bool operator==(const TextFormatKey& other) const
            {
                return family == other.family && size == other.size &&
                    weight == other.weight && italic == other.italic &&
                    underline == other.underline && strikeOut == other.strikeOut &&
                    alignment == other.alignment;
            }
            bool operator<(const TextFormatKey& other) const
            {
                return std::tie(family, size, weight, italic, underline, strikeOut, alignment) <
                    std::tie(other.family, other.size, other.weight, other.italic,
                        other.underline, other.strikeOut, other.alignment);
            }
        };

        struct TextLayoutKey
        {
            TextFormatKey format;
            std::wstring text;
            float boxWidth{};
            bool constrained{};

            bool operator<(const TextLayoutKey& other) const
            {
                return std::tie(format, text, boxWidth, constrained) <
                    std::tie(other.format, other.text, other.boxWidth, other.constrained);
            }
        };

        struct TextLayoutKeyHash
        {
            std::size_t operator()(const TextLayoutKey& key) const
            {
                std::size_t hash = std::hash<std::wstring>{}(key.format.family);
                hash ^= std::hash<std::wstring>{}(key.text) + 0x9e3779b9u + (hash << 6) + (hash >> 2);
                hash ^= std::hash<float>{}(key.format.size) + 0x9e3779b9u + (hash << 6) + (hash >> 2);
                hash ^= static_cast<std::size_t>(key.format.weight) << 1;
                hash ^= static_cast<std::size_t>(key.format.alignment) << 9;
                hash ^= static_cast<std::size_t>(key.constrained);
                return hash;
            }
        };

        struct TextLayoutKeyEqual
        {
            bool operator()(const TextLayoutKey& left, const TextLayoutKey& right) const
            {
                return !(left < right) && !(right < left);
            }
        };

        struct TextLayoutEntry
        {
            Microsoft::WRL::ComPtr<IDWriteTextLayout> natural;
            Microsoft::WRL::ComPtr<IDWriteTextLayout> constrained;
            float naturalWidth{};
            float naturalHeight{};
            std::uint64_t lastUsed{};
        };

        struct BrushKey
        {
            std::uint32_t color{};
            bool operator<(const BrushKey& other) const { return color < other.color; }
        };

        struct CoverCacheEntry
        {
            Microsoft::WRL::ComPtr<ID2D1Bitmap1> bitmap;
            std::uint64_t lastUsed{};
            bool appIcon{};
        };

        Microsoft::WRL::ComPtr<ID2D1Factory1> m_factory;
        Microsoft::WRL::ComPtr<ID2D1Device> m_device;
        Microsoft::WRL::ComPtr<IDWriteFactory> m_writeFactory;
        Microsoft::WRL::ComPtr<IWICImagingFactory> m_wic;
        Microsoft::WRL::ComPtr<IDWriteInlineObject> m_ellipsis;
        Microsoft::WRL::ComPtr<ID2D1DeviceContext> m_context;

        std::map<TextFormatKey, Microsoft::WRL::ComPtr<IDWriteTextFormat>> m_formats;
        std::map<TextLayoutKey, TextLayoutEntry> m_layouts;
        std::map<BrushKey, Microsoft::WRL::ComPtr<ID2D1SolidColorBrush>> m_brushes;
        std::unordered_map<std::wstring, D2D1_COLOR_F> m_colors;
        std::map<std::uint64_t, CoverCacheEntry> m_covers;
        std::unordered_set<TextLayoutKey, TextLayoutKeyHash, TextLayoutKeyEqual> m_usedLayouts;
        std::unordered_set<std::uint64_t> m_usedCoverKeys;
        std::uint64_t m_paintStamp{};

        TextLayoutEntry* GetLayout(ID2D1DeviceContext* context, const EffectiveStyle& style,
            const std::wstring& text, float fontSize, float boxWidth, bool constrained,
            int alignment);
        Microsoft::WRL::ComPtr<IDWriteTextFormat> GetTextFormat(const EffectiveStyle& style,
            float fontSize, int alignment);
        ID2D1SolidColorBrush* GetBrush(ID2D1DeviceContext* context, D2D1_COLOR_F color);
        D2D1_COLOR_F GetColor(const std::wstring& value, D2D1_COLOR_F fallback);
        void DrawTextLine(ID2D1DeviceContext* context, const EffectiveStyle& style,
            const std::wstring& text, float fontSize, float dpiScale, float left, float top, float width,
            float height, int alignment, D2D1_COLOR_F color, float opacity,
            std::optional<double> scanProgress, bool translationPlaceholder);
        void DrawBrowserText(ID2D1DeviceContext* context, const EffectiveStyle& style,
            const std::wstring& text, float left, float top, float width, float height,
            float fontSize, int weight, D2D1_COLOR_F color, float opacity);
        void TrimLayouts();
        void TrimBrushes();
        static Microsoft::WRL::ComPtr<ID2D1Bitmap1> DecodeBitmap(ID2D1DeviceContext* context,
            IWICImagingFactory* wic, const std::wstring& uri, UINT coverEdgePx, bool appIcon,
            std::wstring& error);
    };
}
