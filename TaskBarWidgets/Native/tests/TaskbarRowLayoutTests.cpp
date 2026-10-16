#include "../TrafficMonitor/TaskbarRowLayout.h"
#include <cassert>
#include <iostream>

using namespace TaskbarRowLayout;

static void RectEquals(const Placement& actual, int left, int top, int right, int bottom, bool double_line = false)
{
    assert(actual.left == left && actual.top == top && actual.right == right && actual.bottom == bottom);
    assert(actual.double_line == double_line);
}

int main()
{
    // The second column must start after the wider lower item, not after each
    // row's independent width. Column widths remain independent of other columns.
    const std::vector<Item> paired{{0, 10, 30}, {1, 20, 70}, {0, 20, 40}, {1, 15, 5}};
    auto layout = Calculate(paired, 4, 32, 0);
    RectEquals(layout.items[0], 4, 0, 94, 16);
    RectEquals(layout.items[1], 4, 16, 94, 32);
    RectEquals(layout.items[2], 98, 0, 158, 16);
    RectEquals(layout.items[3], 98, 16, 158, 32);
    assert(layout.width == 162);

    // Non-row-major saved ordering must preserve each row's own sequence.
    layout = Calculate({{1, 10, 50}, {0, 10, 30}, {1, 20, 60}, {0, 10, 10}}, 4, 32, 0);
    RectEquals(layout.items[0], 4, 16, 64, 32);
    RectEquals(layout.items[1], 4, 0, 64, 16);
    RectEquals(layout.items[2], 68, 16, 148, 32);
    RectEquals(layout.items[3], 68, 0, 148, 16);
    assert(layout.width == 152);

    // An explicit unpaired item stays in the selected row; its label/value must
    // not silently switch to the old implicit two-line rendering.
    layout = Calculate({{0, 10, 30}, {1, 20, 70}, {0, 20, 40}}, 4, 32, 0);
    RectEquals(layout.items[2], 98, 0, 158, 16);
    assert(layout.width == 162);
    layout = Calculate({{1, 10, 30}, {1, 20, 40}}, 0, 32, 0);
    RectEquals(layout.items[0], 0, 16, 40, 32);
    RectEquals(layout.items[1], 40, 16, 100, 32);
    assert(layout.width == 100);

    // Full-height row heads are barriers. When both are exclusive, upper goes
    // first; the opposing ordinary item is kept for the next paired column.
    layout = Calculate({{0, 10, 30}, {1, 20, 30, true}, {1, 20, 70}, {0, 60, 80, true}, {1, 30, 50, true}}, 4, 32, 0);
    RectEquals(layout.items[1], 4, 0, 34, 32, true);
    RectEquals(layout.items[0], 38, 0, 128, 16);
    RectEquals(layout.items[2], 38, 16, 128, 32);
    RectEquals(layout.items[3], 132, 0, 212, 32, true);
    RectEquals(layout.items[4], 216, 0, 266, 32, true);
    assert(layout.width == 270);

    // Actual call-site inputs are already DPI-scaled. At 144 DPI, both the gap
    // and measured widths scale once; vertical margins retain the native rule.
    auto scaled = paired;
    for (auto& item : scaled) { item.label_width = item.label_width * 144 / 96; item.value_width = item.value_width * 144 / 96; }
    layout = Calculate(scaled, 6, 48, 6);
    RectEquals(layout.items[0], 6, -3, 141, 21);
    RectEquals(layout.items[1], 6, 27, 141, 51);
    RectEquals(layout.items[2], 147, -3, 237, 21);
    assert(layout.width == 243);

    std::cout << "Native shared-column taskbar geometry passed: unequal widths/gaps, row order/counts, full-height barriers and DPI.\n";
}
