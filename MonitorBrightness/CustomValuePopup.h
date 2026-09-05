#pragma once

#include <windows.h>

#include <functional>

#include "MonitorControl.h"

class CustomValuePopup
{
public:
    bool Create(HINSTANCE instance);
    void Show(MonitorControl& monitors, RECT anchor);
    void Hide(const wchar_t* reason = L"unspecified");
    bool Visible() const { return visible_; }
    void Destroy();

    std::function<void(int)> OnCommit;

private:
    static LRESULT CALLBACK WindowProcThunk(HWND window, UINT message, WPARAM wParam, LPARAM lParam);
    static LRESULT CALLBACK TrackbarSubclassProc(
        HWND trackbar, UINT message, WPARAM wParam, LPARAM lParam,
        UINT_PTR subclassId, DWORD_PTR refData);
    static LRESULT CALLBACK KeyboardHookProc(int code, WPARAM wParam, LPARAM lParam);
    static LRESULT CALLBACK MouseHookProc(int code, WPARAM wParam, LPARAM lParam);
    static bool IsNavigationKey(WPARAM vk);

    LRESULT HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam);
    HWND CreateTrackbar();
    void InstallInputHooks();
    void RemoveInputHooks();
    void HandleScroll(int code, int rawValue);
    LRESULT HandleCustomDraw(NMHDR* header);
    void DrawNameAndPercent(HDC hdc);
    void RepaintNameAndPercent();
    void DrawFooter(HDC hdc);

    HINSTANCE instance_ = nullptr;
    HWND window_ = nullptr;
    HWND trackbar_ = nullptr;
    HFONT font_ = nullptr;
    HBRUSH backgroundBrush_ = nullptr;
    HBRUSH borderBrush_ = nullptr;
    HBRUSH channelBrush_ = nullptr;
    HBRUSH thumbBrush_ = nullptr;

    MonitorControl* monitors_ = nullptr;
    int currentPercent_ = 0;
    int lastCommittedPercent_ = 0;
    bool dragging_ = false;
    bool visible_ = false;

    HHOOK keyboardHook_ = nullptr;
    HHOOK mouseHook_ = nullptr;

    static CustomValuePopup* activeForHooks_;

    static bool loggedPrepaint_;
    static bool loggedChannel_;
    static bool loggedThumb_;
    static bool loggedChannelFallthrough_;
    static bool loggedEraseStage_;
};
