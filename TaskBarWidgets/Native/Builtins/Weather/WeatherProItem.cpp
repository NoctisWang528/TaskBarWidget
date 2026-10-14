#include "pch.h"
#include "WeatherPro.h"
#include "WeatherProItem.h"
#include "DataManager.h"
#include <algorithm>

const wchar_t* CWeatherProItem::GetItemName() const
{
    return WeatherCDataManager::Instance().StringRes(IDS_WEATHER_PRO);
}

const wchar_t* CWeatherProItem::GetItemId() const
{
    return L"FxOP34Lm";
}

const wchar_t* CWeatherProItem::GetItemLableText() const
{
    return L"";
}

const wchar_t* CWeatherProItem::GetItemValueText() const
{
    return L"";
}

const wchar_t* CWeatherProItem::GetItemValueSampleText() const
{
    const auto &config = WeatherCDataManager::Instance().GetConfig();
    if (config.m_show_weather_icon)
    {
        if (config.m_wit == EWeatherInfoType::WEATHER_REALTIME)
            return L"20℃";
        else
            return L"20℃~20℃";
    }
    else
    {
        if (config.m_wit == EWeatherInfoType::WEATHER_REALTIME)
            return L"多云 20℃";
        else
            return L"多云转晴 20℃~20℃";
    }
    return L"";
}

bool CWeatherProItem::IsCustomDraw() const
{
    return true;
}

int CWeatherProItem::GetItemWidthEx(void* hDC) const
{
    CDC* pDC = CDC::FromHandle((HDC)hDC);

    auto icon_width = WeatherCDataManager::Instance().GetConfig().m_show_weather_icon ? WeatherCDataManager::Instance().DPI(20) : 0;

    return icon_width + (std::max)(pDC->GetTextExtent(WeatherCDataManager::Instance().GetWeatherTemperature().c_str()).cx,
                                 pDC->GetTextExtent(GetItemValueSampleText()).cx);
}

void CWeatherProItem::DrawItem(void* hDC, int x, int y, int w, int h, bool dark_mode)
{
    CDC* pDC = CDC::FromHandle((HDC)hDC);

    CRect rect(CPoint(x, y), CSize(w, h));

    if (WeatherCDataManager::Instance().GetConfig().m_show_weather_icon)
    {
        auto icon_size = WeatherCDataManager::Instance().DPI(16);
        auto hIcon = WeatherCDataManager::InstanceRef().GetIcon();
        CPoint icon_pos{ rect.TopLeft() };
        icon_pos.x += WeatherCDataManager::Instance().DPI(2);
        icon_pos.y += (rect.Height() - icon_size) / 2;
        DrawIconEx(pDC->GetSafeHdc(), icon_pos.x, icon_pos.y, hIcon, icon_size, icon_size, 0, NULL, DI_NORMAL);

        rect.left += WeatherCDataManager::Instance().DPI(20);
    }

    pDC->DrawText(WeatherCDataManager::Instance().GetWeatherTemperature().c_str(), rect, DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
}

int CWeatherProItem::OnMouseEvent(MouseEventType type, int x, int y, void* hWnd, int flag)
{
    if (type == IPluginItem::MT_DBCLICKED)
    {
        const auto &dm = WeatherCDataManager::Instance();

        if (dm.GetConfig().m_double_click_action == 0)
            CWeatherPro::Instance().ShowOptionsDialog(hWnd);
        else
            CWeatherPro::Instance().UpdateWeatherInfo(true);

        return 1;
    }

    return 0;
}
