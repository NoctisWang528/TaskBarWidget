#pragma once
#include <memory>

constexpr UINT WM_MEDIA_HOST_WAKE = WM_USER + 1105;
constexpr UINT MEDIA_ANIMATION_TIMER = 1244;

class NativeMediaHost
{
public:
    explicit NativeMediaHost(HWND owner);
    ~NativeMediaHost();
    void Tick();
    void Wheel(int delta);
    void TogglePanel();
    void Menu();
    void GeometryChanged();
    void AppearanceChanged();
    bool MouseInput(UINT message, WPARAM keys, LPARAM position);
    bool IsVisible() const;
    static void Shutdown();
    static void OpenSettings(const wchar_t* page = nullptr);
private:
    struct Impl;
    static std::shared_ptr<Impl>& Shared();
    std::shared_ptr<Impl> m_impl;
};
