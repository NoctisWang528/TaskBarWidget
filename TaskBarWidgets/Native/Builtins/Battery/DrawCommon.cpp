#include "pch.h"
#include "DrawCommon.h"
#include "GdiPlusTool.h"


BatteryCDrawCommon::BatteryCDrawCommon()
{
}

BatteryCDrawCommon::~BatteryCDrawCommon()
{
    SAFE_DELETE(m_pGraphics);
}

void BatteryCDrawCommon::Create(CDC* pDC)
{
    m_pDC = pDC;
    if (pDC != nullptr)
    {
        m_pGraphics = new Gdiplus::Graphics(pDC->GetSafeHdc());
    }
}

void BatteryCDrawCommon::SetDC(CDC* pDC)
{
    m_pDC = pDC;
    SAFE_DELETE(m_pGraphics);
    m_pGraphics = new Gdiplus::Graphics(pDC->GetSafeHdc());
}


void BatteryCDrawCommon::DrawRoundRect(CRect rect, COLORREF color, int radius, BYTE alpha /*= 255*/)
{
    DrawRoundRect(BatteryCGdiPlusTool::CRectToGdiplusRect(rect), BatteryCGdiPlusTool::COLORREFToGdiplusColor(color, alpha), radius);
}

void BatteryCDrawCommon::DrawRoundRect(Gdiplus::RectF rect, Gdiplus::Color color, int radius)
{
    //生成圆角矩形路径
    Gdiplus::GraphicsPath round_rect_path;
    BatteryCGdiPlusTool::CreateRoundRectPath(round_rect_path, rect, static_cast<float>(radius));

    m_pGraphics->SetSmoothingMode(Gdiplus::SmoothingMode::SmoothingModeAntiAlias);      //设置抗锯齿
    Gdiplus::SolidBrush brush(color);
    m_pGraphics->FillPath(&brush, &round_rect_path);          //填充路径
    m_pGraphics->SetSmoothingMode(Gdiplus::SmoothingMode::SmoothingModeNone);
}


void BatteryCDrawCommon::SetDrawArea(CRect rect)
{
    if (m_pDC->GetSafeHdc() == NULL)
        return;
    CRgn rgn;
    rgn.CreateRectRgnIndirect(rect);
    m_pDC->SelectClipRgn(&rgn);
}

HICON BatteryCDrawCommon::LoadIconResource(UINT id, int width, int height)
{
    return (HICON)LoadImage(AfxGetInstanceHandle(), MAKEINTRESOURCE(id), IMAGE_ICON, width, height, 0);
}
