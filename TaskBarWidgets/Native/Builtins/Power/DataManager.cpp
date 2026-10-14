#include "pch.h"
#include "DataManager.h"
#include "Common.h"
#include <vector>
#include <sstream>


PowerCDataManager::PowerCDataManager()
{
	//初始化DPI
	HDC hDC = ::GetDC(HWND_DESKTOP);
	m_dpi = GetDeviceCaps(hDC, LOGPIXELSY);
	::ReleaseDC(HWND_DESKTOP, hDC);
}

PowerCDataManager::~PowerCDataManager()
{

}

PowerCDataManager& PowerCDataManager::Instance()
{
	// Construct only after the MFC application has initialized.
	static PowerCDataManager instance;
	return instance;
}

static void WritePrivateProfileInt(const wchar_t* app_name, const wchar_t* key_name, int value, const wchar_t* file_path)
{
	wchar_t buff[16];
	swprintf_s(buff, L"%d", value);
	WritePrivateProfileString(app_name, key_name, buff, file_path);
}

void PowerCDataManager::LoadConfig(const std::wstring& config_dir)
{
    m_config_path = config_dir + L"PowerMonPlugin.dll.ini";


	//TODO: 在此添加载入配置的代码
	{
		m_setting_data.settings_altered_counter = GetPrivateProfileInt(L"config", L"settings_altered_counter", 0, m_config_path.c_str());
		m_setting_data.is_dbg_mode = (GetPrivateProfileInt(L"config", L"is_dbg_mode", 0, m_config_path.c_str()) != 0);
		m_setting_data.enable_cpu_monitor = (GetPrivateProfileInt(L"config", L"enable_cpu_monitor", 0, m_config_path.c_str()) != 0);
		m_setting_data.enable_gpu_monitor = (GetPrivateProfileInt(L"config", L"enable_gpu_monitor", 0, m_config_path.c_str()) != 0);
		m_setting_data.default_value_unit_space = GetPrivateProfileInt(L"config", L"default_value_unit_space",
			1, m_config_path.c_str());
		m_setting_data.default_max_adaptive_decimal_places = GetPrivateProfileInt(L"config",
			L"default_max_adaptive_decimal_places", 2, m_config_path.c_str());

		::GetPrivateProfileString(L"config", L"pwr_unit_str", L"",
			m_setting_data.pwr_unit_str.GetBuffer(UNIT_STR_MAXLEN + 1),
			UNIT_STR_MAXLEN, m_config_path.c_str());
		::GetPrivateProfileString(L"config", L"electric_capacity_unit_str", L"",
			m_setting_data.electric_capacity_unit_str.GetBuffer(UNIT_STR_MAXLEN + 1),
			UNIT_STR_MAXLEN, m_config_path.c_str());

		::GetPrivateProfileString(L"config", L"electric_voltage_unit_str", L"",
			m_setting_data.electric_voltage_unit_str.GetBuffer(UNIT_STR_MAXLEN + 1),
			UNIT_STR_MAXLEN, m_config_path.c_str());
		::GetPrivateProfileString(L"config", L"hour_unit_str", L"",
			m_setting_data.hour_unit_str.GetBuffer(UNIT_STR_MAXLEN + 1),
			UNIT_STR_MAXLEN, m_config_path.c_str());
		::GetPrivateProfileString(L"config", L"minute_unit_str", L"",
			m_setting_data.minute_unit_str.GetBuffer(UNIT_STR_MAXLEN + 1),
			UNIT_STR_MAXLEN, m_config_path.c_str());

		::GetPrivateProfileString(L"config", L"nan_str", L"",
			m_setting_data.nan_str.GetBuffer(UNIT_STR_MAXLEN + 1),
			UNIT_STR_MAXLEN, m_config_path.c_str());

		bool isFirstInital = FirstInitalCheck(m_setting_data), altered_flag = false;
		if (isFirstInital) {

			altered_flag = true;

#if WINRT_USE_FLAG
			m_setting_data.enable_cpu_monitor = 1;
			m_setting_data.enable_gpu_monitor = 1;

#endif
		}

		if (isFirstInital ||
			m_setting_data.pwr_unit_str.GetLength() >= UNIT_STR_MAXLEN) {
			m_setting_data.pwr_unit_str = "W";
			altered_flag = true;
		}
		if (isFirstInital ||
			m_setting_data.electric_capacity_unit_str.GetLength() >= UNIT_STR_MAXLEN) {
			m_setting_data.electric_capacity_unit_str = "Wh";
			altered_flag = true;
		}
		if (isFirstInital ||
			m_setting_data.electric_voltage_unit_str.GetLength() >= UNIT_STR_MAXLEN) {
			m_setting_data.electric_voltage_unit_str = "V";
			altered_flag = true;
		}
		if (isFirstInital ||
			m_setting_data.hour_unit_str.GetLength() >= UNIT_STR_MAXLEN) {
			m_setting_data.hour_unit_str = "H";
			altered_flag = true;
		}
		if (isFirstInital ||
			m_setting_data.minute_unit_str.GetLength() >= UNIT_STR_MAXLEN) {
			m_setting_data.minute_unit_str = "M";
			altered_flag = true;
		}

		if (isFirstInital ||
			m_setting_data.nan_str.GetLength() >= UNIT_STR_MAXLEN) {
			m_setting_data.nan_str = "NaN";
			altered_flag = true;
		}

		if (altered_flag) {
			m_setting_data.settings_altered_counter += 1;
			SaveConfig();
		}
	}
}



void PowerCDataManager::SaveConfig() const
{
	if (!m_config_path.empty())
	{
		//TODO: 在此添加保存配置的代码 
		WritePrivateProfileInt(L"config", L"settings_altered_counter",
			(m_setting_data.settings_altered_counter), m_config_path.c_str());
		WritePrivateProfileInt(L"config", L"is_dbg_mode",
			(m_setting_data.is_dbg_mode), m_config_path.c_str());
		WritePrivateProfileInt(L"config", L"enable_cpu_monitor",
			(m_setting_data.enable_cpu_monitor), m_config_path.c_str());
		WritePrivateProfileInt(L"config", L"enable_gpu_monitor",
			(m_setting_data.enable_gpu_monitor), m_config_path.c_str());
		WritePrivateProfileInt(L"config", L"default_value_unit_space",
			(m_setting_data.default_value_unit_space), m_config_path.c_str());
		WritePrivateProfileInt(L"config", L"default_max_adaptive_decimal_places",
			(m_setting_data.default_max_adaptive_decimal_places), m_config_path.c_str());

		WritePrivateProfileString(L"config", L"pwr_unit_str",
			m_setting_data.pwr_unit_str, m_config_path.c_str());
		WritePrivateProfileString(L"config", L"electric_capacity_unit_str",
			m_setting_data.electric_capacity_unit_str, m_config_path.c_str());
		WritePrivateProfileString(L"config", L"electric_voltage_unit_str",
			m_setting_data.electric_voltage_unit_str, m_config_path.c_str());
		WritePrivateProfileString(L"config", L"hour_unit_str",
			m_setting_data.hour_unit_str, m_config_path.c_str());
		WritePrivateProfileString(L"config", L"minute_unit_str",
			m_setting_data.minute_unit_str, m_config_path.c_str());
		WritePrivateProfileString(L"config", L"nan_str",
			m_setting_data.nan_str, m_config_path.c_str());
	}
}

bool PowerCDataManager::FirstInitalCheck(PowerSettingData setting_data) {

	return setting_data.settings_altered_counter <= 0;
}

const CString& PowerCDataManager::StringRes(UINT id)
{
	auto iter = m_string_table.find(id);
	if (iter != m_string_table.end())
	{
		return iter->second;
	}
	else
	{
		m_string_table[id].LoadString(GetModuleHandleW(nullptr), id);
		return m_string_table[id];
	}
}

void PowerCDataManager::DPIFromWindow(CWnd* pWnd)
{
	CWindowDC dc(pWnd);
	HDC hDC = dc.GetSafeHdc();
	m_dpi = GetDeviceCaps(hDC, LOGPIXELSY);
}

int PowerCDataManager::DPI(int pixel)
{
	return m_dpi * pixel / 96;
}

float PowerCDataManager::DPIF(float pixel)
{
	return m_dpi * pixel / 96;
}

int PowerCDataManager::RDPI(int pixel)
{
	return pixel * 96 / m_dpi;
}

HICON PowerCDataManager::GetIcon(UINT id)
{
	auto iter = m_icons.find(id);
	if (iter != m_icons.end())
	{
		return iter->second;
	}
	else
	{
		HICON hIcon = (HICON)LoadImage(AfxGetInstanceHandle(), MAKEINTRESOURCE(id), IMAGE_ICON, DPI(16), DPI(16), 0);
		m_icons[id] = hIcon;
		return hIcon;
	}
}
