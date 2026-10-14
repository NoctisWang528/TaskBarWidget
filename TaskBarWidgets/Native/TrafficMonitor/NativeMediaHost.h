#pragma once
#include <memory>

class NativeMediaHost
{
public:
    explicit NativeMediaHost(HWND owner);
    ~NativeMediaHost();
    void Tick();
    void Wheel(int delta);
    void TogglePanel();
    void Menu();
    bool MouseInput(UINT message, WPARAM keys, LPARAM position);
    bool IsVisible() const;
    static void Shutdown();
    static void OpenSettings(const wchar_t* page = nullptr);
private:
    struct Impl;
    static std::shared_ptr<Impl>& Shared();
    std::shared_ptr<Impl> m_impl;
};
