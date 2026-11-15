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
    MigratePeripheralConfig();
}

CSettingsHelper::CSettingsHelper(const std::wstring& file_path)
    : CIniHelper(file_path)
{
    MigratePeripheralConfig();
}

void CSettingsHelper::LoadPeripheralAliases()
{
    PeripheralTaskbar::AliasMap aliases;
    GetAllKeyValues(L"peripheral_aliases", aliases);
    try { PeripheralTaskbar::Provider::Instance().RestoreAliases(aliases); }
    catch (const std::invalid_argument&) { return; } // An invalid local graph is never partially restored.
    MigratePeripheralConfig();
}

void CSettingsHelper::MigratePeripheralConfig()
{
    using namespace PeripheralTaskbar;
    const auto& aliases = Provider::Instance().Aliases();
    if (aliases.empty()) return;
    auto sections = GetAllAppName(L"");
    // GetAllAppName scans newline-prefixed headers; include a first-line header too.
    if (m_ini_str.starts_with(L"["))
    {
        const auto end = m_ini_str.find(L']');
        if (end != std::wstring::npos) sections.push_back(m_ini_str.substr(1, end - 1));
    }
    for (const auto& section : sections)
    {
        const bool labels = section == L"plugin_display_str_taskbar_window" || section == L"plugin_display_str_main_window";
        const bool colors = section.ends_with(L"_peripherals") &&
            (section.starts_with(L"task_bar_") || section.starts_with(L"taskbar_default_style_"));
        if (!labels && !colors) continue;
        std::map<std::wstring, std::wstring> values;
        GetAllKeyValues(section, values);
        // GetAllKeyValues omits unquoted empty values. They are intentional
        // configuration too, and must win over a retired nonempty label.
        for (const auto& [oldId, retained] : aliases)
            for (const auto& id : {oldId, retained})
                for (const auto* suffix : {L"", L".label", L".value"})
                {
                    if (labels != (*suffix == L'\0')) continue;
                    const auto key = (labels ? L"peripheral_" : L"builtin_peripheral_") + id + suffix;
                    std::wstring value;
                    if (GetString(section.c_str(), key.c_str(), value)) values[key] = value;
                }
        const auto canonical = CanonicalMap(aliases, values);
        if (canonical == values) continue;
        // Remove only retired keys. Rebuilding a section would discard comments
        // and unrelated explicitly empty settings that the legacy parser skips.
        const auto header = m_ini_str.find(L"[" + section + L"]");
        auto line = m_ini_str.find(L'\n', header);
        while (line != std::wstring::npos && line + 1 < m_ini_str.size() && m_ini_str[line + 1] != L'[')
        {
            const auto end = m_ini_str.find(L'\n', line + 1);
            const auto equal = m_ini_str.find(L'=', line + 1);
            std::wstring key;
            if (equal < end)
            {
                key = m_ini_str.substr(line + 1, equal - line - 1);
                CCommon::StringNormalize(key);
            }
            if (CanonicalItemId(aliases, key) != key)
                m_ini_str.erase(line + 1, end == std::wstring::npos ? std::wstring::npos : end - line);
            else line = end;
        }
        for (const auto& [key, value] : canonical)
            if (labels) WriteString(section.c_str(), key.c_str(), value);
            else _WriteString(section.c_str(), key.c_str(), value);
    }
    for (const auto* key : {L"item_order_ids", L"plugin_display_item"})
    {
        std::wstring value;
        if (GetString(L"task_bar", key, value)) WriteString(L"task_bar", key, CanonicalList(aliases, value));
    }
    std::vector<std::wstring> entries;
    CCommon::StringSplit(GetString(L"task_bar", L"item_rows", L""), L',', entries);
    std::map<std::wstring, std::wstring> rows;
    for (const auto& entry : entries)
    {
        const auto split = entry.rfind(L':');
        if (split != std::wstring::npos) rows.emplace(entry.substr(0, split), entry.substr(split + 1));
    }
    if (!rows.empty())
    {
        std::wstring serialized;
        for (const auto& [key, value] : CanonicalMap(aliases, rows))
        {
            if (!serialized.empty()) serialized += L",";
            serialized += key + L":" + value;
        }
        WriteString(L"task_bar", L"item_rows", serialized);
    }
    RemoveSection(L"peripheral_aliases");
    for (const auto& [oldId, retained] : aliases) WriteString(L"peripheral_aliases", oldId.c_str(), retained);
}

void CSettingsHelper::CanonicalizePeripheralItems(TaskBarSettingData& data)
{
    using namespace PeripheralTaskbar;
    auto& provider = Provider::Instance();
    const auto& aliases = provider.Aliases();
    if (aliases.empty()) return;
    data.plugin_display_item.FromString(CanonicalList(aliases, data.plugin_display_item.ToString()));
    data.item_rows = CanonicalMap(aliases, data.item_rows);
    const auto order = CanonicalList(aliases, data.item_order.ToStableString());
    data.item_order.Init(); // An open legacy settings draft can still cache retired pointers/indices.
    data.item_order.FromStableString(order);
    const auto canonical = [&](const CommonDisplayItem& item)
    {
        if (!IsPeripheral(item)) return item;
        return CommonDisplayItem(provider.Ensure(DeviceId(item.PluginItem()->GetItemId())));
    };
    const auto colors = data.text_colors;
    data.text_colors.clear();
    for (const auto& [item, color] : colors)
        if (canonical(item) == item) data.text_colors.emplace(item, color);
    for (const auto& [item, color] : colors) data.text_colors.try_emplace(canonical(item), color);
    const auto labels = data.disp_str.GetAllItems();
    data.disp_str = {};
    for (const auto& [item, label] : labels)
        if (canonical(item) == item) data.disp_str.Get(item) = label;
    for (const auto& [item, label] : labels)
        if (!data.disp_str.GetAllItems().contains(canonical(item))) data.disp_str.Get(canonical(item)) = label;
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
    std::map<std::wstring, TaskbarItemColor> peripheralColors;
    for (auto iter = text_colors.begin(); iter != text_colors.end(); ++iter)
    {
        if (IsPeripheral(iter->first))
        {
            peripheralColors.emplace(iter->first.StableId(), iter->second);
            continue;
        }
        CString tmp;
        tmp.Format(_T("%d,%d,"), iter->second.label, iter->second.value);
        str += tmp;
    }
    const auto section = PeripheralColorSection(AppName, KeyName);
    for (const auto& [id, color] : PeripheralTaskbar::CanonicalMap(
        PeripheralTaskbar::Provider::Instance().Aliases(), peripheralColors))
    {
        WriteInt(section.c_str(), (id + L".label").c_str(), color.label);
        WriteInt(section.c_str(), (id + L".value").c_str(), color.value);
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
    data.text_colors.try_emplace(display, TaskbarItemColor{
        static_cast<COLORREF>(GetInt(section.c_str(), (id + L".label").c_str(), fallback.label)),
        static_cast<COLORREF>(GetInt(section.c_str(), (id + L".value").c_str(), fallback.value))});
    if (!data.disp_str.GetAllItems().contains(display))
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
        if (!PeripheralTaskbar::Provider::Instance().Active(plugin)) continue;
        std::wstring str{ plugin->GetItemLableText() };
        bool exist = GetString(AppName, plugin->GetItemId(), str);
        //主窗口只读取配置文件中存在的项，任务栏窗口读取所有项
        if (!is_main_window || exist)
            disp_str.Load(plugin->GetItemId(), str);
    }
}

void CSettingsHelper::SavePluginDisplayStr(const wchar_t* AppName, const DispStrings& disp_str)
{
    std::map<std::wstring, std::wstring> labels;
    for (const auto& [item, label] : disp_str.GetAllItems())
        if (item.IsPlugin()) labels.emplace(item.PluginItem()->GetItemId(), label);
    for (const auto& [id, label] : PeripheralTaskbar::CanonicalMap(
        PeripheralTaskbar::Provider::Instance().Aliases(), labels))
        WriteString(AppName, id.c_str(), label);
}
