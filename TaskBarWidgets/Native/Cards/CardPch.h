#pragma once
// Shared precompiled header for the native card module. The card sources compile
// without the MFC PCH: they belong to the STA card thread and must not pull in
// MFC windows, document classes or data managers.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <shellscalingapi.h>

#include <d2d1_3.h>
#include <d2d1_3helper.h>
#include <d3d11_1.h>
#include <dwrite_3.h>
#include <dxgi1_3.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <winrt/Microsoft.UI.h>
#include <winrt/Microsoft.UI.Composition.SystemBackdrops.h>

// System composition interop interfaces (drawing surface, graphics device,
// desktop window target).
#include <windows.ui.composition.interop.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace wx = winrt::Windows;
namespace wxc = winrt::Windows::UI::Composition;
namespace muxc = winrt::Microsoft::UI::Composition::SystemBackdrops;
