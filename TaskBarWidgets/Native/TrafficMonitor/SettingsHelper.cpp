#include "stdafx.h"
#include "SettingsHelper.h"
#include "TrafficMonitor.h"
#include "../Builtins/Peripherals/PeripheralTaskbar.h"

namespace
{
    bool IsPeripheral(const CommonDisplayItem& item)
    { return item.IsPlugin() && !PeripheralTaskbar::DeviceId(item.PluginItem()->GetItemId()).empty(); }
    std::wstring PeripheralColorSection(const wchar_t* section, const wchar_t* key)
    { return std::wstring(section) + L"_" + key + L"_peripherals"; }
}

CSettingsHelper::CSettingsHelper()
    : CIniHelper(theApp.m_config_path)
{
}

CSettingsHelper::CSettingsHelper(const std::wstring& file_path)
    : CIniHelper(file_path)
{
}

void CSettingsHelper::SaveFontData(const wchar_t* AppName, const FontInfo& font)
{
    WriteString(AppName, L"font_name", wstring(font.name));
    WriteInt(AppName, L"font_size", font.size);
    bool style[4];
    style[0] = font.bold;
    style[1] = font.italic;
    style[2] = font.underline;
    style[3] = font.strike_out;
    WriteBoolArray(AppName, L"font_style", style, 4);
}

void CSettingsHelper::LoadFontData(const wchar_t* AppName, FontInfo& font, const FontInfo& default_font) const
{
    font.name = GetString(AppName, L"font_name", default_font.name).c_str();
    font.size = GetInt(AppName, L"font_size", default_font.size);
    bool style[4];
    GetBoolArray(AppName, L"font_style", style, 4);
    font.bold = style[0];
    font.italic = style[1];
    font.underline = style[2];
    font.strike_out = style[3];
}

void CSettingsHelper::LoadMainWndColors(const wchar_t* AppName, const wchar_t* KeyName, const std::set<CommonDisplayItem>& all_items, std::map<CommonDisplayItem, COLORREF>& text_colors, COLORREF default_color)
{
    CString default_str;
    default_str.Format(_T("%d"), default_color);
    wstring str{ default_str.GetString() };
    _GetString(AppName, KeyName, str);
    std::vector<wstring> split_result;
    CCommon::StringSplit(str, L',', split_result);
    size_t index = 0;
    const std::set<CommonDisplayItem>& items{ all_items.empty() ? theApp.m_plugins.AllDisplayItemsWithPlugins() : all_items };
    for (auto iter = items.begin(); iter != items.end(); ++iter)
    {
        if (index < split_result.size())
            text_colors[*iter] = _wtoi(split_result[index].c_str());
        else if (!split_result.empty())
            text_colors[*iter] = _wtoi(split_result[0].c_str());
        else
            text_colors[*iter] = default_color;
        index++;
    }
}

void CSettingsHelper::SaveMainWndColors(const wchar_t* AppName, const wchar_t* KeyName, const std::map<CommonDisplayItem, COLORREF>& text_colors)
{
    CString str;
    for (auto iter = text_colors.begin(); iter != text_colors.end(); ++iter)
    {
        CString tmp;
        tmp.Format(_T("%d,"), iter->second);
        str += tmp;
    }
    _WriteString(AppName, KeyName, wstring(str));
}

void CSettingsHelper::LoadTaskbarWndColors(const wchar_t* AppName, const wchar_t* KeyName, std::map<CommonDisplayItem, TaskbarItemColor>& text_colors, const wchar_t* default_str)
{
    wstring str{ default_str };
    _GetString(AppName, KeyName, str);
    std::vector<wstring> split_result;
    CCommon::StringSplit(str, L',', split_result);
    size_t index = 0;
    COLORREF default_color = _wtoi(default_str);
    for (auto iter = theApp.m_plugins.AllDisplayItemsWithPlugins().begin(); iter != theApp.m_plugins.AllDisplayItemsWithPlugins().end(); ++iter)
    {
        if (IsPeripheral(*iter))
        {
            const auto section = PeripheralColorSection(AppName, KeyName);
            const auto id = iter->StableId();
            text_colors[*iter].label = GetInt(section.c_str(), (id + L".label").c_str(), default_color);
            text_colors[*iter].value = GetInt(section.c_str(), (id + L".value").c_str(), default_color);
            continue; // Dynamic discovery order must never shift legacy color slots.
        }
        if (index < split_result.size())
            text_colors[*iter].label = _wtoi(split_result[index].c_str());
        //else if (!split_result.empty())
        //    text_colors[*iter].label = _wtoi(split_result[0].c_str());
        else
            text_colors[*iter].label = default_color;

        if (index + 1 < split_result.size())
            text_colors[*iter].value = _wtoi(split_result[index + 1].c_str());
        //else if (split_result.size() > 1)
        //    text_colors[*iter].value = _wtoi(split_result[1].c_str());
        else
            text_colors[*iter].value = default_color;
        index += 2;
    }
}

void CSettingsHelper::LoadTaskbarWndColors(const wchar_t* AppName, const wchar_t* KeyName, std::map<CommonDisplayItem, TaskbarItemColor>& text_colors, COLORREF default_color)
{
    CString default_str;
    default_str.Format(_T("%d"), default_color);
    LoadTaskbarWndColors(AppName, KeyName, text_colors, default_str.GetString());
}

void CSettingsHelper::SaveTaskbarWndColors(const wchar_t* AppName, const wchar_t* KeyName, const std::map<CommonDisplayItem, TaskbarItemColor>& text_colors)
{
    CString str;
    for (auto iter = text_colors.begin(); iter != text_colors.end(); ++iter)
    {
        if (IsPeripheral(iter->first))
        {
            const auto section = PeripheralColorSection(AppName, KeyName);
            const auto id = iter->first.StableId();
            WriteInt(section.c_str(), (id + L".label").c_str(), iter->second.label);
            WriteInt(section.c_str(), (id + L".value").c_str(), iter->second.value);
            continue;
        }
        CString tmp;
        tmp.Format(_T("%d,%d,"), iter->second.label, iter->second.value);
        str += tmp;
    }
    _WriteString(AppName, KeyName, wstring(str));
}

void CSettingsHelper::LoadPeripheralTaskbarItem(IPluginItem* item, TaskBarSettingData& data)
{
    const CommonDisplayItem display(item);
    const auto section = PeripheralColorSection(L"task_bar", L"task_bar_text_color");
    const auto id = display.StableId();
    const auto fallback = data.text_colors.empty() ? TaskbarItemColor{static_cast<COLORREF>(data.dft_text_colors), static_cast<COLORREF>(data.dft_text_colors)}
        : data.text_colors.begin()->second;
    data.text_colors[display].label = GetInt(section.c_str(), (id + L".label").c_str(), fallback.label);
    data.text_colors[display].value = GetInt(section.c_str(), (id + L".value").c_str(), fallback.value);
    data.disp_str.Get(display) = GetString(L"plugin_display_str_taskbar_window", item->GetItemId(), item->GetItemLableText());
}

void CSettingsHelper::LoadDisplayStr(const wchar_t* AppName, DispStrings& disp_str, bool is_main_window) const
{
    for (auto display_item : AllDisplayItems)
    {
        std::wstring str{ CommonDisplayItem(display_item).DefaultString(is_main_window)};
        bool exist = GetString(AppName, CommonDisplayItem(display_item).GetItemIniKeyName(), str);
        //主窗口只读取配置文件中存在的项，任务栏窗口读取所有项
        if (!is_main_window || exist)
            disp_str.Get(display_item) = str;
    }
}

void CSettingsHelper::SaveDisplayStr(const wchar_t* AppName, const DispStrings& disp_str)
{
    for (const auto& item : disp_str.GetAllItems())
    {
        if (!item.first.IsPlugin())
        {
            WriteString(AppName, item.first.GetItemIniKeyName(), item.second);
        }
    }
}

void CSettingsHelper::LoadPluginDisplayStr(const wchar_t* AppName, DispStrings& disp_str, bool is_main_window)
{
    for (const auto& plugin : theApp.m_plugins.GetPluginItems())
    {
        std::wstring str{ plugin->GetItemLableText() };
        bool exist = GetString(AppName, plugin->GetItemId(), str);
        //主窗口只读取配置文件中存在的项，任务栏窗口读取所有项
        if (!is_main_window || exist)
            disp_str.Load(plugin->GetItemId(), str);
    }
}

void CSettingsHelper::SavePluginDisplayStr(const wchar_t* AppName, const DispStrings& disp_str)
{
    for (const auto& plugin : theApp.m_plugins.GetPluginItems())
    {
        if (disp_str.GetAllItems().find(plugin) != disp_str.GetAllItems().end())
            WriteString(AppName, plugin->GetItemId(), disp_str.GetConst(plugin));
    }
}
