#pragma once
// Small drawing helpers shared by the card painters: theme palette, solid
// brushes, rounded panels, text with trailing ellipsis and simple glyph layout.

#include "CardIcons.h"
#include "CardLayout.h"
#include "CardText.h"
#include "CardWindow.h"

#include <Windows.h>
#include <d2d1_3.h>
#include <dwrite_3.h>

#include <string>

namespace Cards::Draw
{
    struct Palette
    {
        Color primary;
        Color secondary;
        Color accent;
        Color border;
        Color panel;
        Color track;
        Color fill;
    };

    Palette Colors(const CardEnvironment& environment);

    ID2D1SolidColorBrush* Brush(ID2D1DeviceContext* context, Color color);
    void FillRect(ID2D1DeviceContext* context, D2D1_RECT_F rect, Color color);
    void FillRound(ID2D1DeviceContext* context, D2D1_RECT_F rect, float radius, Color color);
    // Draw hover, press and focus before the foreground, using the original shape. The host
    // supplies only an eligible pressed id after resolving pointer capture.
    void ControlBackground(ID2D1DeviceContext* context, D2D1_RECT_F rect, float radius,
        Color base, Color accent, const Paint::Interaction& interaction, int id, bool enabled = true);
    void StrokeRound(ID2D1DeviceContext* context, D2D1_RECT_F rect, float radius, Color color, float width);
    void Line(ID2D1DeviceContext* context, D2D1_POINT_2F from, D2D1_POINT_2F to, Color color, float width);

    // Text drawn with the chip's typography rules. Alignment defaults to leading;
    // \`ellipsis\` clips with a trailing ellipsis when the text is too wide.
    void Text(ID2D1DeviceContext* context, Graphics& graphics, const std::wstring& value,
        const std::wstring& family, float size, int weight, Color color, D2D1_RECT_F rect,
        DWRITE_TEXT_ALIGNMENT alignment = DWRITE_TEXT_ALIGNMENT_LEADING,
        bool italic = false, bool underline = false, bool strikeout = false,
        DWRITE_PARAGRAPH_ALIGNMENT vertical = DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
    std::wstring Ellipsize(ID2D1DeviceContext* context, Graphics& graphics, const std::wstring& value,
        const std::wstring& family, float size, int weight, float maxWidth);

    // A capsule pill (used by the update card and the source row).
    void Pill(ID2D1DeviceContext* context, D2D1_RECT_F rect, Color color);

    // Progress rail: track plus filled portion plus optional knob.
    void Progress(ID2D1DeviceContext* context, D2D1_RECT_F rect, float ratio, Color track,
        Color fill, bool knob, bool focused = false, Color focusColor = {});

    // Interaction-table helper so painters never hand-roll ids.
    int AddButton(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& action,
        const std::wstring& name, bool enabled = true, bool focusable = true);
    int AddSlider(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& action,
        const std::wstring& name, bool enabled = true);
    int AddCell(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& action,
        const std::wstring& name, bool enabled, bool selected);
    int AddText(Layout::Table& regions, int id, D2D1_RECT_F rect, const std::wstring& name);

    D2D1_RECT_F Rect(float left, float top, float right, float bottom);
}

namespace Cards::Paint
{
    float WeatherHeight(const Snapshot& snapshot, const CardEnvironment& environment, float widthDip);
}
