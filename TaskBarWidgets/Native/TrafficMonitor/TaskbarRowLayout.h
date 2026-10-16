#pragma once
#include <algorithm>
#include <array>
#include <vector>

namespace TaskbarRowLayout
{
    struct Item
    {
        int row{}, label_width{}, value_width{};
        bool exclusive{};
    };
    struct Placement
    {
        int left{}, top{}, right{}, bottom{};
        bool double_line{};
    };
    struct Result
    {
        std::vector<Placement> items;
        int width{};
    };

    // Widths, gap, height and margin are already physical pixels. Keep each row's
    // chosen order; only the paired column shares its maximum measured width.
    inline Result Calculate(const std::vector<Item>& items, int gap, int height, int margin)
    {
        Result result{std::vector<Placement>(items.size()), gap};
        std::array<std::vector<size_t>, 2> rows;
        for (size_t index = 0; index < items.size(); ++index)
            rows[std::clamp(items[index].row, 0, 1)].push_back(index);
        size_t next[2]{};
        const int row_height = height / 2;
        const int y[2]{-margin / 2, -margin / 2 + row_height + margin};
        while (next[0] < rows[0].size() || next[1] < rows[1].size())
        {
            int exclusive_row = -1;
            for (int row = 0; row < 2; ++row)
                if (next[row] < rows[row].size() && items[rows[row][next[row]]].exclusive)
                {
                    exclusive_row = row;
                    break; // Upper wins when both row heads require the full column.
                }
            if (exclusive_row >= 0)
            {
                const size_t index = rows[exclusive_row][next[exclusive_row]++];
                const int width = (std::max)(items[index].label_width, items[index].value_width);
                result.items[index] = {result.width, 0, result.width + width, height, true};
                result.width += width + gap;
                continue;
            }
            int width = 0;
            for (int row = 0; row < 2; ++row)
                if (next[row] < rows[row].size())
                {
                    const auto& item = items[rows[row][next[row]]];
                    width = (std::max)(width, item.label_width + item.value_width);
                }
            for (int row = 0; row < 2; ++row)
                if (next[row] < rows[row].size())
                {
                    const size_t index = rows[row][next[row]++];
                    result.items[index] = {result.width, y[row], result.width + width, y[row] + row_height, false};
                }
            result.width += width + gap;
        }
        return result;
    }
}
