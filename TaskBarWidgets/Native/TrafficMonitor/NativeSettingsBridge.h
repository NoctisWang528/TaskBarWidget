#pragma once
#include "TrafficMonitorDlg.h"
#include "NativeSettingsValidation.h"
#include "PluginSettingsBridge.h"
#include "TaskbarHelper.h"
#include "auto_start_helper.h"
#include "../Builtins/Weather/WeatherService.h"
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <functional>
#include <cmath>
#include <numeric>

// Descriptors and setters share one table: the Web page cannot write arbitrary INI keys.
class NativeSettingsBridge
{
    using JsonObject = winrt::Windows::Data::Json::JsonObject;
    using JsonArray = winrt::Windows::Data::Json::JsonArray;
    using JsonValue = winrt::Windows::Data::Json::JsonValue;
    using IJsonValue = winrt::Windows::Data::Json::IJsonValue;
    using JsonValueType = winrt::Windows::Data::Json::JsonValueType;
    struct Field
    {
        JsonObject descriptor;
        std::function<void(const IJsonValue&)> set;
    };
    TaskBarSettingData taskbar = theApp.m_taskbar_data;
    GeneralSettingData general = theApp.m_general_data;
    StartupRegistrationState startup = get_startup_registration_for_this_user();
    MainConfigData config = theApp.m_cfg_data;
    bool transparent = taskbar.IsTaskbarTransparent();
    std::vector<Field> fields;
    std::vector<int> ranks;
    std::vector<CommonDisplayItem> items;
    JsonArray groups;
    PluginSettingsBridge pluginSettings;

    static void SetStartup(bool enabled)
    {
        if (!theApp.SetAutoRun(enabled, true))
            throw std::runtime_error("Windows startup registration failed (error " + std::to_string(theApp.m_startup_error) + "). Actual registration has been read back; retry or repair the startup entry.");
    }

    static JsonValue Text(const std::wstring& value) { return JsonValue::CreateStringValue(value); }
    static void Invalid() { throw std::invalid_argument("Setting value has an invalid type or is outside its allowed range."); }
    void Group(const wchar_t* id, const wchar_t* label, const wchar_t* page)
    {
        JsonObject group;
        group.SetNamedValue(L"id", Text(id)); group.SetNamedValue(L"label", Text(label)); group.SetNamedValue(L"page", Text(page));
        groups.Append(group);
    }
    Field& Add(const std::wstring& key, const std::wstring& label, const wchar_t* group, const wchar_t* type,
        const IJsonValue& value, std::function<void(const IJsonValue&)> setter)
    {
        JsonObject descriptor;
        descriptor.SetNamedValue(L"key", Text(key)); descriptor.SetNamedValue(L"label", Text(label));
        descriptor.SetNamedValue(L"group", Text(group)); descriptor.SetNamedValue(L"type", Text(type)); descriptor.SetNamedValue(L"value", value);
        fields.push_back({ descriptor, std::move(setter) }); return fields.back();
    }
    void Boolean(const std::wstring& key, const std::wstring& label, const wchar_t* group, bool& value)
    {
        Add(key,label,group,L"boolean",JsonValue::CreateBooleanValue(value),[&value](const IJsonValue& input)
        { if(input.ValueType()!=JsonValueType::Boolean) Invalid(); value=input.GetBoolean(); });
    }
    void Number(const std::wstring& key, const std::wstring& label, const wchar_t* group, int& value, int minimum, int maximum)
    {
        auto& field=Add(key,label,group,L"number",JsonValue::CreateNumberValue(value),[&value,minimum,maximum](const IJsonValue& input)
        { if(input.ValueType()!=JsonValueType::Number) Invalid(); double number=input.GetNumber();
          if(!NativeSettingsValidation::Integer(number,minimum,maximum)) Invalid(); value=static_cast<int>(number); });
        field.descriptor.SetNamedValue(L"min",JsonValue::CreateNumberValue(minimum)); field.descriptor.SetNamedValue(L"max",JsonValue::CreateNumberValue(maximum));
    }
    void String(const std::wstring& key, const std::wstring& label, const wchar_t* group, const std::wstring& value,
        std::function<void(const std::wstring&)> setter, const std::vector<std::wstring>& options={})
    {
        auto& field=Add(key,label,group,options.empty()?L"text":L"select",Text(value),[setter,options](const IJsonValue& input)
        { if(input.ValueType()!=JsonValueType::String) Invalid(); std::wstring text=input.GetString().c_str();
          if(text.size()>1024 || text.find_first_of(L"\r\n")!=std::wstring::npos || text.find(L'\0')!=std::wstring::npos) Invalid();
          if(!options.empty() && std::find(options.begin(),options.end(),text)==options.end()) Invalid(); setter(text); });
        if(!options.empty())
        {
            JsonArray list; for(const auto& option:options) { JsonObject entry; entry.SetNamedValue(L"value",Text(option)); entry.SetNamedValue(L"label",Text(option.empty()?L"自动 / 平均":option)); list.Append(entry); }
            field.descriptor.SetNamedValue(L"options",list);
        }
    }
    void String(const std::wstring& key, const std::wstring& label, const wchar_t* group, std::wstring& value, const std::vector<std::wstring>& options={})
    { String(key,label,group,value,[&value](const std::wstring& input){value=input;},options); }
    template<typename T> void Choice(const std::wstring& key, const std::wstring& label, const wchar_t* group, T& value, std::initializer_list<const wchar_t*> labels)
    {
        JsonArray options; int index=0;
        for(const auto* label:labels) { JsonObject option; option.SetNamedValue(L"value",JsonValue::CreateNumberValue(index++)); option.SetNamedValue(L"label",Text(label)); options.Append(option); }
        auto& field=Add(key,label,group,L"select",JsonValue::CreateNumberValue(static_cast<int>(value)),[&value,index](const IJsonValue& input)
        { if(input.ValueType()!=JsonValueType::Number) Invalid(); double number=input.GetNumber(); if(!NativeSettingsValidation::Integer(number,0,index-1)) Invalid(); value=static_cast<T>(static_cast<int>(number)); });
        field.descriptor.SetNamedValue(L"options",options);
    }
    void Color(const std::wstring& key, const std::wstring& label, const wchar_t* group, COLORREF& value)
    {
        wchar_t color[8]; swprintf_s(color,L"#%02x%02x%02x",GetRValue(value),GetGValue(value),GetBValue(value));
        Add(key,label,group,L"color",Text(color),[&value](const IJsonValue& input)
        { if(input.ValueType()!=JsonValueType::String) Invalid(); std::wstring text=input.GetString().c_str();
          if(!NativeSettingsValidation::Color(text)) Invalid();
          unsigned rgb=wcstoul(text.c_str()+1,nullptr,16); value=RGB((rgb>>16)&255,(rgb>>8)&255,rgb&255); });
    }
    void Hardware(const wchar_t* key, const wchar_t* label, HardwareItem item)
    {
        Add(key,label,L"sensors",L"boolean",JsonValue::CreateBooleanValue(general.IsHardwareEnable(item)),[this,item](const IJsonValue& input)
        {if(input.ValueType()!=JsonValueType::Boolean) Invalid(); general.SetHardwareEnable(item,input.GetBoolean());});
    }
public:
    NativeSettingsBridge()
    {
        general.auto_run = startup.Enabled();
        Group(L"placement",L"任务栏位置与布局",L"taskbar"); Group(L"values",L"数值、单位与图表",L"taskbar");
        Group(L"appearance",L"共用任务栏外观",L"taskbar"); Group(L"monitorAppearance",L"监测颜色与绘制",L"taskbar"); Group(L"items",L"显示项、插件与排序",L"taskbar");
        Group(L"network",L"网络连接",L"hardware"); Group(L"sensors",L"硬件监测与传感器",L"hardware");
        Group(L"nativeGeneral",L"监测常规设置",L"general"); Group(L"builtins.battery",L"电池显示",L"hardware"); Group(L"builtins.power",L"电池与硬件功耗",L"hardware"); Group(L"builtins.weather",L"天气",L"hardware");
#define B(member,label,group) Boolean(std::wstring(L"taskbar.")+winrt::to_hstring(#member).c_str(),label,group,taskbar.member)
#define N(member,label,group,min,max) Number(std::wstring(L"taskbar.")+winrt::to_hstring(#member).c_str(),label,group,taskbar.member,min,max)
        std::vector<HWND> secondary_displays; CTaskbarHelper::GetAllSecondaryDisplayTaskbar(secondary_displays);
        JsonArray display_options;
        int current_display=taskbar.show_taskbar_wnd_in_secondary_display?taskbar.secondary_display_index+1:0;
        int display_count=(std::max)(static_cast<int>(secondary_displays.size()),current_display);
        for(int index=0;index<=display_count;++index)
        {JsonObject option;option.SetNamedValue(L"value",JsonValue::CreateNumberValue(index));option.SetNamedValue(L"label",Text(index==0?L"主显示器":L"副显示器 "+std::to_wstring(index)));display_options.Append(option);}
        auto& display=Add(L"taskbar.display",L"显示器",L"placement",L"select",JsonValue::CreateNumberValue(current_display),[this,display_count](const IJsonValue& input)
        {if(input.ValueType()!=JsonValueType::Number || !NativeSettingsValidation::Integer(input.GetNumber(),0,display_count)) Invalid();int selected=static_cast<int>(input.GetNumber());taskbar.show_taskbar_wnd_in_secondary_display=selected>0;taskbar.secondary_display_index=selected>0?selected-1:0;});
        display.descriptor.SetNamedValue(L"options",display_options);
        B(tbar_wnd_on_left,L"靠任务栏左侧",L"placement"); B(tbar_wnd_snap,L"贴靠居中应用图标",L"placement"); B(horizontal_arrange,L"水平单行排列",L"placement");
        N(item_space,L"项目间距",L"placement",0,32); N(vertical_margin,L"垂直间距",L"placement",-10,10);
        N(window_offset_top,L"垂直偏移",L"placement",-20,20); N(window_offset_left,L"水平偏移",L"placement",-800,800);
        B(avoid_overlap_with_widgets,L"避开 Windows 小组件",L"placement"); N(taskbar_left_space_win11,L"左侧保留宽度",L"placement",0,4096); N(taskbar_right_space_win11,L"右侧保留宽度",L"placement",0,4096);
        B(value_right_align,L"数值右对齐",L"values"); N(digits_number,L"数值位数",L"values",3,7); B(speed_short_mode,L"简洁网速",L"values"); B(unit_byte,L"使用字节 B（关闭为比特 b）",L"values");
        Choice(L"taskbar.speed_unit",L"网速单位",L"values",taskbar.speed_unit,{L"自动",L"KB/s",L"MB/s"});
        Choice(L"taskbar.memory_display",L"内存显示",L"values",taskbar.memory_display,{L"使用率",L"已使用内存",L"可用内存"});
        B(hide_unit,L"隐藏单位",L"values"); B(hide_percent,L"隐藏百分号",L"values"); B(separate_value_unit_with_space,L"数值与单位空格分隔",L"values");
        B(show_status_bar,L"显示占用率图表",L"values"); B(cm_graph_type,L"滚动曲线图（关闭为柱状图）",L"values"); B(show_graph_dashed_box,L"图表虚线边框",L"values");
        B(show_netspeed_figure,L"显示网速图表",L"values"); N(netspeed_figure_max_value,L"网速图表最大值",L"values",1,1000000);
        Choice(L"taskbar.netspeed_figure_max_value_unit",L"网速图表最大值单位",L"values",taskbar.netspeed_figure_max_value_unit,{L"KB/s",L"MB/s"}); B(graph_color_following_system,L"图表跟随系统主题色",L"values");
        String(L"taskbar.fontName",L"字体",L"appearance",std::wstring(taskbar.font.name),[this](const std::wstring& value){if(value.empty()||value.size()>=LF_FACESIZE) Invalid(); taskbar.font.name=value.c_str();});
        Number(L"taskbar.fontSize",L"字号",L"appearance",taskbar.font.size,5,72);
        Boolean(L"taskbar.fontBold",L"粗体",L"appearance",taskbar.font.bold); Boolean(L"taskbar.fontItalic",L"斜体",L"appearance",taskbar.font.italic);
        Boolean(L"taskbar.fontUnderline",L"下划线",L"appearance",taskbar.font.underline); Boolean(L"taskbar.fontStrikeOut",L"删除线",L"appearance",taskbar.font.strike_out);
        Color(L"taskbar.back_color",L"背景颜色",L"appearance",taskbar.back_color); Color(L"taskbar.transparent_color",L"透明颜色",L"monitorAppearance",taskbar.transparent_color); Color(L"taskbar.status_bar_color",L"图表颜色",L"monitorAppearance",taskbar.status_bar_color);
        Boolean(L"taskbar.transparent",L"任务栏背景透明",L"appearance",transparent);
        B(specify_each_item_color,L"每项使用独立颜色",L"monitorAppearance"); B(auto_adapt_light_theme,L"自动适应深浅主题",L"monitorAppearance"); B(auto_set_background_color,L"使用任务栏背景色",L"monitorAppearance");
        N(dark_default_style,L"深色主题预设序号",L"monitorAppearance",-1,TASKBAR_DEFAULT_STYLE_NUM-1); N(light_default_style,L"浅色主题预设序号",L"monitorAppearance",-1,TASKBAR_DEFAULT_STYLE_NUM-1);
        B(auto_save_taskbar_color_settings_to_preset,L"颜色变化保存到当前主题预设",L"monitorAppearance"); B(enable_colorful_emoji,L"彩色 Emoji",L"monitorAppearance");
        Number(L"taskbar.backgroundOpacity",L"背景不透明度（%）",L"appearance",taskbar.backgroundOpacity,0,100);
#undef B
#undef N
        for(const auto& item:theApp.m_plugins.AllDisplayItemsWithPlugins()) items.push_back(item);
        ranks.resize(items.size());
        const auto& order=taskbar.item_order.GetItemOrderConst();
        int legacy_row=0;
        for(const auto& item:taskbar.item_order.GetAllDisplayItemsWithOrder())
        {
            const auto id=item.StableId();
            if(taskbar.item_rows.find(id)==taskbar.item_rows.end()) taskbar.item_rows[id]=item.IsDoubleLineExclusive()?0:legacy_row;
            const bool shown=item.IsPlugin()?taskbar.plugin_display_item.Contains(item.PluginItem()->GetItemId()):taskbar.display_item.Contains(item.ItemType());
            if(shown) legacy_row=item.IsDoubleLineExclusive()?0:1-legacy_row;
        }
        for(size_t index=0;index<items.size();++index)
        {
            auto position=std::find(order.begin(),order.end(),static_cast<int>(index)); ranks[index]=position==order.end()?static_cast<int>(index+1):static_cast<int>(position-order.begin()+1);
            auto item=items[index]; auto prefix=L"items."+item.StableId(); std::wstring label=item.GetItemName().GetString();
            bool shown=item.IsPlugin()?taskbar.plugin_display_item.Contains(item.PluginItem()->GetItemId()):taskbar.display_item.Contains(item.ItemType());
            Add(prefix+L".show",label+L" · 显示",L"items",L"boolean",JsonValue::CreateBooleanValue(shown),[this,item](const IJsonValue& input)
            {if(input.ValueType()!=JsonValueType::Boolean) Invalid(); bool value=input.GetBoolean(); if(item.IsPlugin()) taskbar.plugin_display_item.SetStrContained(item.PluginItem()->GetItemId(),value); else if(value) taskbar.display_item.Add(item.ItemType()); else taskbar.display_item.Remove(item.ItemType());});
            Number(prefix+L".order",label+L" · 顺序",L"items",ranks[index],1,static_cast<int>(items.size()));
            auto& row=Add(prefix+L".row",label+L" · 所在条",L"items",L"number",JsonValue::CreateNumberValue(taskbar.item_rows[item.StableId()]),[this,item](const IJsonValue& input)
            {
                if(input.ValueType()!=JsonValueType::Number || !NativeSettingsValidation::Integer(input.GetNumber(),0,1)) Invalid();
                taskbar.item_rows[item.StableId()]=static_cast<int>(input.GetNumber()); taskbar.explicit_item_rows=true;
            });
            row.descriptor.SetNamedValue(L"min",JsonValue::CreateNumberValue(0));row.descriptor.SetNamedValue(L"max",JsonValue::CreateNumberValue(1));
            String(prefix+L".label",label+L" · 标签",L"items",taskbar.disp_str.Get(item));
            Color(prefix+L".labelColor",label+L" · 标签颜色",L"items",taskbar.text_colors[item].label); Color(prefix+L".valueColor",label+L" · 数值颜色",L"items",taskbar.text_colors[item].value);
        }
        Boolean(L"network.autoSelect",L"自动选择活动网卡",L"network",config.m_auto_select); Boolean(L"network.selectAll",L"统计全部网卡",L"network",config.m_select_all);
        Boolean(L"general.show_all_interface",L"列出全部网络接口",L"network",general.show_all_interface);
        std::vector<NetWorkConection> adapters; CAdapterCommon::GetAdapterInfo(adapters);
        std::vector<std::wstring> adapter_names{CCommon::StrToUnicode(config.m_connection_name.c_str())};
        for(size_t index=0;index<adapters.size();++index)
        {
            std::wstring name=CCommon::StrToUnicode(adapters[index].description.c_str()); if(std::find(adapter_names.begin(),adapter_names.end(),name)==adapter_names.end()) adapter_names.push_back(name);
            Add(L"network.adapter."+std::to_wstring(index),name+L" · 列出",L"network",L"boolean",JsonValue::CreateBooleanValue(!general.connections_hide.Contains(name)),[this,name](const IJsonValue& input)
            {if(input.ValueType()!=JsonValueType::Boolean) Invalid(); general.connections_hide.SetStrContained(name,!input.GetBoolean());});
        }
        String(L"network.selected",L"选定网卡",L"network",CCommon::StrToUnicode(config.m_connection_name.c_str()),[this](const std::wstring& value){config.m_connection_name=CCommon::UnicodeToStr(value.c_str());},adapter_names);
        Number(L"general.monitor_time_span",L"采样间隔（毫秒）",L"sensors",general.monitor_time_span,MONITOR_TIME_SPAN_MIN,MONITOR_TIME_SPAN_MAX);
        Choice(L"general.cpu_usage_acquire_method",L"CPU 使用率采集",L"sensors",general.cpu_usage_acquire_method,{L"CPU 时间",L"性能计数器"});
        Hardware(L"general.cpuEnabled",L"CPU 硬件监测",HI_CPU); Hardware(L"general.gpuEnabled",L"GPU 硬件监测",HI_GPU); Hardware(L"general.diskEnabled",L"硬盘硬件监测",HI_HDD); Hardware(L"general.mainboardEnabled",L"主板硬件监测",HI_MBD);
        std::vector<std::wstring> cpu_names{general.cpu_core_name,L""}, disk_names{general.hard_disk_name,L""};
        if(auto* dialog=CTrafficMonitorDlg::Instance();dialog && dialog->IsGetDiskUsageByPdh()) for(const auto& name:dialog->GetPdhDiskUsageHelper().GetDiskNames()) disk_names.push_back(name.GetString());
#ifndef WITHOUT_TEMPERATURE
        { CSingleLock lock(&theApp.m_minitor_lib_critical); if(lock.Lock(0) && theApp.m_pMonitor) {for(const auto& pair:theApp.m_pMonitor->AllCpuTemperature()) cpu_names.push_back(pair.first); for(const auto& pair:theApp.m_pMonitor->AllHDDTemperature()) disk_names.push_back(pair.first);} }
#endif
        CCommon::RemoveVectorDuplicateItem(cpu_names); CCommon::RemoveVectorDuplicateItem(disk_names);
        String(L"general.cpu_core_name",L"CPU 温度传感器",L"sensors",general.cpu_core_name,cpu_names); String(L"general.hard_disk_name",L"监测硬盘",L"sensors",general.hard_disk_name,disk_names);
        Boolean(L"general.show_notify_icon",L"显示通知区域图标",L"nativeGeneral",general.show_notify_icon);
        Boolean(L"general.auto_run",L"登录 Windows 时启动",L"nativeGeneral",general.auto_run);
        std::vector<std::wstring> languages{general.language.bcp_47};
        for(const auto& language:theApp.m_str_table.GetLanguageList()) languages.push_back(language.bcp_47);
        CCommon::RemoveVectorDuplicateItem(languages);
        String(L"general.language",L"监测语言（重启后生效）",L"nativeGeneral",general.language.bcp_47,[this](const std::wstring& value)
        {for(const auto& language:theApp.m_str_table.GetLanguageList()) if(language.bcp_47==value){general.language=language;return;}if(value!=general.language.bcp_47) Invalid();},languages);
        for(const auto& descriptor:pluginSettings.Fields())
        {
            auto object=descriptor.GetObject(); std::wstring key=object.GetNamedString(L"key").c_str();
            fields.push_back({object,[this,key](const IJsonValue& input){pluginSettings.Set(key,input);}});
        }
        for(auto& field:fields)
        {
            std::wstring key=field.descriptor.GetNamedString(L"key").c_str();
            if(key==L"taskbar.disable_d2d" || key==L"taskbar.enable_colorful_emoji" || key==L"taskbar.dark_default_style" || key==L"taskbar.light_default_style"
                || key==L"taskbar.transparent_color" || key==L"taskbar.auto_save_taskbar_color_settings_to_preset" || key==L"general.cpu_usage_acquire_method"
                || key==L"taskbar.double_click_action" || key==L"taskbar.double_click_exe")
                field.descriptor.SetNamedValue(L"advanced",JsonValue::CreateBooleanValue(true));
            if(key.rfind(L"items.",0)==0)
            {
                size_t start=6,end=key.find(L'.',start);
                const auto id=key.substr(start,end-start);
                const auto found=std::find_if(items.begin(),items.end(),[&](const auto& item){return item.StableId()==id;});
                if(found==items.end()) continue;
                const size_t index=static_cast<size_t>(found-items.begin());
                field.descriptor.SetNamedValue(L"itemId",Text(id));
                field.descriptor.SetNamedValue(L"itemName",Text(std::wstring(items[index].GetItemName())));
                field.descriptor.SetNamedValue(L"sampleValue",Text(std::wstring(items[index].GetItemValueSampleText(false))));
                field.descriptor.SetNamedValue(L"isPlugin",JsonValue::CreateBooleanValue(items[index].IsPlugin()));
                field.descriptor.SetNamedValue(L"doubleLineExclusive",JsonValue::CreateBooleanValue(items[index].IsDoubleLineExclusive()));
            }
        }
    }
    JsonObject Describe() const
    {
        JsonObject payload; JsonArray descriptors; for(const auto& field:fields) descriptors.Append(field.descriptor);
        JsonObject registration;
        registration.SetNamedValue(L"enabled",JsonValue::CreateBooleanValue(startup.Enabled()));
        registration.SetNamedValue(L"needsRepair",JsonValue::CreateBooleanValue(startup.NeedsRepair()));
        registration.SetNamedValue(L"registeredPath",Text(startup.taskPath.empty()?startup.legacyPath:startup.taskPath));
        registration.SetNamedValue(L"legacyPath",Text(startup.legacyPath));
        registration.SetNamedValue(L"currentPath",Text(startup.currentPath));
        const HRESULT failure=FAILED(startup.error)?startup.error:HRESULT_FROM_WIN32(theApp.m_startup_error);
        registration.SetNamedValue(L"status",Text(FAILED(failure)?L"error":startup.NeedsRepair()?L"repairRequired":startup.Enabled()?L"enabled":L"disabled"));
        wchar_t failureText[128]{};
        if(FAILED(failure)) swprintf_s(failureText,L"Windows 启动项操作失败 (0x%08X)",static_cast<unsigned>(failure));
        registration.SetNamedValue(L"error",Text(failureText));
        payload.SetNamedValue(L"startup",registration);
        payload.SetNamedValue(L"plugins",pluginSettings.Describe()); payload.SetNamedValue(L"groups",groups); payload.SetNamedValue(L"fields",descriptors); payload.SetNamedValue(L"configPath",Text(theApp.m_config_path)); return payload;
    }
    void Apply(const JsonObject& values)
    {
        if(values.HasKey(L"general.repair_startup"))
        {
            if(values.Size()!=1 || values.GetNamedValue(L"general.repair_startup").ValueType()!=JsonValueType::Boolean || !values.GetNamedBoolean(L"general.repair_startup")) Invalid();
            SetStartup(true);
            return;
        }
        for(const auto& pair:values)
        {
            auto found=std::find_if(fields.begin(),fields.end(),[&](const Field& field){return field.descriptor.GetNamedString(L"key")==pair.Key();});
            if(found==fields.end()) throw std::invalid_argument("Unknown setting key; reload the settings page.");
            found->set(pair.Value());
        }
        std::vector<int> order(items.size()); std::iota(order.begin(),order.end(),0);
        std::stable_sort(order.begin(),order.end(),[this](int left,int right){return ranks[left]<ranks[right];}); taskbar.item_order.SetOrder(order); taskbar.show_tool_tip=false;
        taskbar.SetTaskabrTransparent(transparent);
        if(!taskbar.disable_d2d && !CTaskBarDlgDrawCommonSupport::CheckSupport()) throw std::invalid_argument("Direct2D is unavailable on this system.");
        auto previous=theApp.m_cfg_data; theApp.m_cfg_data=config;
        const bool startupRequested=values.HasKey(L"general.auto_run");
        const bool startupEnabled=general.auto_run;
        // Startup is an explicit OS operation, applied after configuration validation and saving.
        general.auto_run=theApp.m_general_data.auto_run;
        general.auto_run_by_task_scheduler=theApp.m_general_data.auto_run_by_task_scheduler;
        auto* dialog=CTrafficMonitorDlg::Instance();
        bool network_changed=config.m_connection_name!=previous.m_connection_name || config.m_auto_select!=previous.m_auto_select || config.m_select_all!=previous.m_select_all;
        bool applied{}; std::wstring error;
        try
        {
            pluginSettings.Save();
            if(!dialog || !dialog->ApplyUnifiedSettings(taskbar,general,network_changed,&applied,&error))
                throw std::runtime_error(winrt::to_string(error.empty()?L"Could not save the native configuration.":error));
        }
        catch (...)
        {
            if(!applied) theApp.m_cfg_data=previous;
            pluginSettings.Rollback();
            throw;
        }
        if(startupRequested) SetStartup(startupEnabled);
        for (const auto& pair : values) {
            if (std::wstring_view(pair.Key()).starts_with(L"builtins.weatherpro.dll.")) {
                WeatherService::Instance().StartRefresh();
                break;
            }
        }
    }
};
