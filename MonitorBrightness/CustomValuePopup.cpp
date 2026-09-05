#include "CustomValuePopup.h"

#include <commctrl.h>

#include <algorithm>
#include <string>

#include "Config.h"

namespace
{

constexpr wchar_t kPopupClass[] = L"MonitorBrightness_CustomValuePopup";

constexpr int kPopupWidth = 300;
constexpr int kPopupHeight = 130;
constexpr int kTrackMin = 0;
constexpr int kTrackMax = 100;

constexpr wchar_t kFooterText[] = L"Hold Ctrl to Snap to Nearest 5%.";
constexpr int kCtrlExtraGap = 4;

constexpr int kScreenMargin = 8;

constexpr COLORREF kBackground = RGB(0x2b, 0x2b, 0x2b);
constexpr COLORREF kBorder = RGB(80, 80, 80);
constexpr COLORREF kText = RGB(240, 240, 240);
constexpr COLORREF kMuted = RGB(180, 180, 180);
constexpr COLORREF kChannel = RGB(60, 60, 60);
constexpr COLORREF kAccent = RGB(0x00, 0x78, 0xd4);
constexpr COLORREF kChipFill = RGB(0x3a, 0x3a, 0x3a);

int Clamp(int value, int low, int high)
{
    return value < low ? low : (value > high ? high : value);
}

int SnapTo5(int percent)
{
    return Clamp(((percent + 2) / 5) * 5, 0, 100);
}

} // namespace

CustomValuePopup* CustomValuePopup::activeForHooks_ = nullptr;
bool CustomValuePopup::loggedPrepaint_ = false;
bool CustomValuePopup::loggedChannel_ = false;
bool CustomValuePopup::loggedThumb_ = false;
bool CustomValuePopup::loggedChannelFallthrough_ = false;
bool CustomValuePopup::loggedEraseStage_ = false;

bool CustomValuePopup::Create(HINSTANCE instance)
{
    instance_ = instance;

    INITCOMMONCONTROLSEX icc{};
    icc.dwSize = sizeof(icc);
    icc.dwICC = ICC_BAR_CLASSES;
    if (!InitCommonControlsEx(&icc))
    {
        Log(L"CustomValuePopup: InitCommonControlsEx failed (Win32 %lu) - continuing anyway, "
            L"the trackbar class may already be registered regardless.", GetLastError());
    }

    backgroundBrush_ = CreateSolidBrush(kBackground);
    if (backgroundBrush_ == nullptr)
    {
        Log(L"CustomValuePopup: CreateSolidBrush (background) failed (Win32 %lu).", GetLastError());
    }

    borderBrush_ = CreateSolidBrush(kBorder);
    if (borderBrush_ == nullptr)
    {
        Log(L"CustomValuePopup: CreateSolidBrush (border) failed (Win32 %lu).", GetLastError());
    }

    channelBrush_ = CreateSolidBrush(kChannel);
    if (channelBrush_ == nullptr)
    {
        Log(L"CustomValuePopup: CreateSolidBrush (channel) failed (Win32 %lu).", GetLastError());
    }

    thumbBrush_ = CreateSolidBrush(kAccent);
    if (thumbBrush_ == nullptr)
    {
        Log(L"CustomValuePopup: CreateSolidBrush (thumb) failed (Win32 %lu).", GetLastError());
    }

    NONCLIENTMETRICSW metrics{};
    metrics.cbSize = sizeof(metrics);
    if (SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0))
    {
        font_ = CreateFontIndirectW(&metrics.lfMessageFont);
        if (font_ == nullptr)
        {
            Log(L"CustomValuePopup: CreateFontIndirectW failed (Win32 %lu).", GetLastError());
        }
    }

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = &CustomValuePopup::WindowProcThunk;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = kPopupClass;
    windowClass.hbrBackground = backgroundBrush_;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);

    if (RegisterClassExW(&windowClass) == 0)
    {
        Log(L"CustomValuePopup: RegisterClassExW failed (Win32 %lu).", GetLastError());
        return false;
    }

    window_ = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kPopupClass, L"Custom Value",
        WS_POPUP, 0, 0, kPopupWidth, kPopupHeight,
        nullptr, nullptr, instance, this);

    if (window_ == nullptr)
    {
        Log(L"CustomValuePopup: CreateWindowExW (main popup window) failed (Win32 %lu).", GetLastError());
        return false;
    }

    trackbar_ = CreateTrackbar();
    if (trackbar_ == nullptr)
    {
        return false;
    }

    SetWindowSubclass(trackbar_, &CustomValuePopup::TrackbarSubclassProc, 1,
        reinterpret_cast<DWORD_PTR>(this));

    Log(L"CustomValuePopup: created OK.");
    return true;
}

void CustomValuePopup::Destroy()
{
    RemoveInputHooks();

    if (window_ != nullptr)
    {
        DestroyWindow(window_);
        window_ = nullptr;
    }

    if (!UnregisterClassW(kPopupClass, instance_))
    {
        Log(L"CustomValuePopup: UnregisterClassW failed (Win32 %lu).", GetLastError());
    }
}

HWND CustomValuePopup::CreateTrackbar()
{
    HWND handle = CreateWindowExW(
        0, TRACKBAR_CLASSW, L"",
        WS_CHILD | WS_VISIBLE | WS_TABSTOP | TBS_HORZ | TBS_NOTICKS | TBS_TRANSPARENTBKGND,
        20, 40, kPopupWidth - 40, 30,
        window_, nullptr, instance_, nullptr);

    if (handle == nullptr)
    {
        Log(L"CustomValuePopup: CreateWindowExW (trackbar) failed (Win32 %lu).", GetLastError());
        return nullptr;
    }

    SendMessageW(handle, TBM_SETRANGE, TRUE, MAKELPARAM(kTrackMin, kTrackMax));
    return handle;
}

void CustomValuePopup::Show(MonitorControl& monitors, RECT anchor)
{
    if (window_ == nullptr)
    {
        return;
    }

    monitors_ = &monitors;

    int startPercent = 50;
    for (const auto& monitor : monitors.Monitors())
    {
        if (monitor.supported)
        {
            startPercent = monitor.Percent();
            break;
        }
    }

    currentPercent_ = Clamp(startPercent, kTrackMin, kTrackMax);
    lastCommittedPercent_ = currentPercent_;
    dragging_ = false;

    SendMessageW(trackbar_, TBM_SETPOS, TRUE, currentPercent_);

    int x = anchor.left - 3; // aligns with the menu's visually drawn left edge
    int y = anchor.bottom;

    const POINT anchorPoint{ anchor.left, anchor.top };
    const HMONITOR monitor = MonitorFromPoint(anchorPoint, MONITOR_DEFAULTTONEAREST);
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    if (monitor != nullptr && GetMonitorInfoW(monitor, &info))
    {
        const RECT& work = info.rcWork;
        x = Clamp(x, work.left + kScreenMargin, std::max<int>(work.left + kScreenMargin, work.right - kPopupWidth - kScreenMargin));
        y = Clamp(y, work.top + kScreenMargin, std::max<int>(work.top + kScreenMargin, work.bottom - kPopupHeight - kScreenMargin));
    }

    Log(L"CustomValuePopup::Show: anchor=(%ld,%ld,%ld,%ld), placing at (%d,%d) %dx%d.",
        anchor.left, anchor.top, anchor.right, anchor.bottom, x, y, kPopupWidth, kPopupHeight);

    SetWindowPos(window_, HWND_TOPMOST, x, y, kPopupWidth, kPopupHeight, SWP_NOACTIVATE);
    ShowWindow(window_, SW_SHOWNA);

    InstallInputHooks();
    visible_ = true;

    InvalidateRect(window_, nullptr, TRUE);
}

void CustomValuePopup::Hide(const wchar_t* reason)
{
    if (!visible_)
    {
        return;
    }

    Log(L"CustomValuePopup::Hide(): %s", reason);

    visible_ = false;
    ShowWindow(window_, SW_HIDE);
    RemoveInputHooks();

    // Closes whatever menu is still tracking; a safe no-op if nothing is.
    EndMenu();
}

LRESULT CALLBACK CustomValuePopup::TrackbarSubclassProc(
    HWND trackbar, UINT message, WPARAM wParam, LPARAM lParam,
    UINT_PTR /*subclassId*/, DWORD_PTR refData)
{
    if (message == WM_KEYDOWN && wParam == VK_ESCAPE)
    {
        auto* self = reinterpret_cast<CustomValuePopup*>(refData);
        self->Hide(L"Escape, via the trackbar subclass");
        return 0;
    }

    return DefSubclassProc(trackbar, message, wParam, lParam);
}

void CustomValuePopup::InstallInputHooks()
{
    activeForHooks_ = this;

    if (keyboardHook_ == nullptr)
    {
        keyboardHook_ = SetWindowsHookExW(WH_KEYBOARD_LL,
            &CustomValuePopup::KeyboardHookProc, nullptr, 0);

        if (keyboardHook_ == nullptr)
        {
            Log(L"CustomValuePopup: SetWindowsHookExW (WH_KEYBOARD_LL) failed (Win32 %lu).", GetLastError());
        }
    }

    if (mouseHook_ == nullptr)
    {
        mouseHook_ = SetWindowsHookExW(WH_MOUSE_LL,
            &CustomValuePopup::MouseHookProc, nullptr, 0);

        if (mouseHook_ == nullptr)
        {
            Log(L"CustomValuePopup: SetWindowsHookExW (WH_MOUSE_LL) failed (Win32 %lu).", GetLastError());
        }
    }
}

void CustomValuePopup::RemoveInputHooks()
{
    if (keyboardHook_ != nullptr)
    {
        UnhookWindowsHookEx(keyboardHook_);
        keyboardHook_ = nullptr;
    }

    if (mouseHook_ != nullptr)
    {
        UnhookWindowsHookEx(mouseHook_);
        mouseHook_ = nullptr;
    }

    if (activeForHooks_ == this)
    {
        activeForHooks_ = nullptr;
    }
}

bool CustomValuePopup::IsNavigationKey(WPARAM vk)
{
    switch (vk)
    {
    case VK_LEFT: case VK_RIGHT: case VK_UP: case VK_DOWN:
    case VK_HOME: case VK_END: case VK_PRIOR: case VK_NEXT:
    case VK_ESCAPE:
        return true;
    default:
        return false;
    }
}

LRESULT CALLBACK CustomValuePopup::KeyboardHookProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION && activeForHooks_ != nullptr &&
        (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
    {
        auto* hook = reinterpret_cast<KBDLLHOOKSTRUCT*>(lParam);

        if (IsNavigationKey(hook->vkCode))
        {
            SendMessageW(activeForHooks_->trackbar_, WM_KEYDOWN, hook->vkCode, 0);
            return 1;
        }

        // WH_KEYBOARD_LL reports the side-specific VK_LCONTROL/VK_RCONTROL for
        // Ctrl, never plain VK_CONTROL; VK_SNAPSHOT is excluded so a screenshot
        // doesn't close the popup out from under itself.
        if (hook->vkCode != VK_CONTROL && hook->vkCode != VK_LCONTROL && hook->vkCode != VK_RCONTROL &&
            hook->vkCode != VK_SNAPSHOT)
        {
            wchar_t reason[64];
            _snwprintf_s(reason, _TRUNCATE, L"keyboard hook, vk=0x%02lX", hook->vkCode);
            activeForHooks_->Hide(reason);
        }
    }

    return CallNextHookEx(nullptr, code, wParam, lParam);
}

LRESULT CALLBACK CustomValuePopup::MouseHookProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION && activeForHooks_ != nullptr &&
        (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN ||
         wParam == WM_NCLBUTTONDOWN || wParam == WM_NCRBUTTONDOWN ||
         wParam == WM_MOUSEMOVE))
    {
        auto* hook = reinterpret_cast<MSLLHOOKSTRUCT*>(lParam);

        if (wParam == WM_MOUSEMOVE)
        {
            // Forwarded directly into the trackbar while dragging - the native
            // dispatch queue stalls under the still-tracking menu otherwise.
            if (activeForHooks_->dragging_)
            {
                POINT client = hook->pt;
                ScreenToClient(activeForHooks_->trackbar_, &client);
                SendMessageW(activeForHooks_->trackbar_, WM_MOUSEMOVE,
                    MK_LBUTTON, MAKELPARAM(client.x, client.y));
            }

            return CallNextHookEx(nullptr, code, wParam, lParam);
        }

        RECT rect{};
        const BOOL gotRect = GetWindowRect(activeForHooks_->window_, &rect);
        const bool inside = gotRect && PtInRect(&rect, hook->pt);

        if (gotRect && !inside)
        {
            activeForHooks_->Hide(L"mouse hook, click outside the popup's rect");
            return CallNextHookEx(nullptr, code, wParam, lParam);
        }

        // The still-tracking menu underneath owns this screen region for
        // hit-testing purposes, so a real click never reaches the trackbar -
        // inject a synthetic WM_LBUTTONDOWN straight into it instead.
        if (inside && wParam == WM_LBUTTONDOWN)
        {
            RECT trackbarRect{};
            if (GetWindowRect(activeForHooks_->trackbar_, &trackbarRect) && PtInRect(&trackbarRect, hook->pt))
            {
                POINT client = hook->pt;
                ScreenToClient(activeForHooks_->trackbar_, &client);
                Log(L"CustomValuePopup mouse hook: injecting synthetic WM_LBUTTONDOWN into the trackbar at "
                    L"client (%ld,%ld) - real click may still be getting swallowed by the still-tracking menu.",
                    client.x, client.y);
                SendMessageW(activeForHooks_->trackbar_, WM_LBUTTONDOWN,
                    MK_LBUTTON, MAKELPARAM(client.x, client.y));
            }
        }

        // Every click landing in the popup's own rect is eaten here so none of
        // them can leak through to the menu underneath.
        return 1;
    }

    return CallNextHookEx(nullptr, code, wParam, lParam);
}

LRESULT CustomValuePopup::HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message)
    {
    case WM_ERASEBKGND:
    {
        HDC hdc = reinterpret_cast<HDC>(wParam);
        RECT client;
        GetClientRect(window, &client);
        FillRect(hdc, &client, backgroundBrush_);
        FrameRect(hdc, &client, borderBrush_);
        DrawNameAndPercent(hdc);
        DrawFooter(hdc);
        return 1;
    }

    case WM_CTLCOLORSTATIC:
        SetTextColor(reinterpret_cast<HDC>(wParam), kText);
        SetBkMode(reinterpret_cast<HDC>(wParam), TRANSPARENT);
        return reinterpret_cast<LRESULT>(backgroundBrush_);

    case WM_MOUSEACTIVATE:
        // MA_NOACTIVATE: the click still passes through, so the trackbar's
        // own SetCapture keeps working.
        return MA_NOACTIVATE;

    case WM_HSCROLL:
        HandleScroll(LOWORD(wParam), HIWORD(wParam));
        return 0;

    case WM_NOTIFY:
    {
        auto* header = reinterpret_cast<NMHDR*>(lParam);
        if (header->code == NM_CUSTOMDRAW)
        {
            return HandleCustomDraw(header);
        }
        return 0;
    }

    case WM_KEYDOWN:
        if (wParam == VK_ESCAPE)
        {
            Hide(L"Escape, direct to the popup window");
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);

    case WM_DESTROY:
        RemoveInputHooks();
        if (backgroundBrush_ != nullptr) DeleteObject(backgroundBrush_);
        if (borderBrush_ != nullptr) DeleteObject(borderBrush_);
        if (channelBrush_ != nullptr) DeleteObject(channelBrush_);
        if (thumbBrush_ != nullptr) DeleteObject(thumbBrush_);
        if (font_ != nullptr) DeleteObject(font_);
        return 0;

    default:
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

void CustomValuePopup::HandleScroll(int code, int rawValue)
{
    const bool live = (code == TB_THUMBTRACK || code == TB_THUMBPOSITION);
    const bool snap = (GetKeyState(VK_CONTROL) & 0x8000) != 0;

    int value = live ? rawValue : static_cast<int>(SendMessageW(trackbar_, TBM_GETPOS, 0, 0));

    if (live)
    {
        dragging_ = true;

        if (snap)
        {
            value = SnapTo5(value);
            SendMessageW(trackbar_, TBM_SETPOS, TRUE, value);
        }
    }
    else if (dragging_)
    {
        dragging_ = false;
        value = static_cast<int>(SendMessageW(trackbar_, TBM_GETPOS, 0, 0));
    }
    else if (snap)
    {
        // Steps a clean +-5 from the last committed value, using
        // direction-aware floor/ceil so a value that isn't itself a multiple
        // of 5 doesn't get part of the step eaten by rounding first.
        const int direction = (value > lastCommittedPercent_) ? 1
                             : (value < lastCommittedPercent_) ? -1 : 0;

        int target = lastCommittedPercent_;
        if (direction > 0)
        {
            target = (lastCommittedPercent_ / 5 + 1) * 5;
        }
        else if (direction < 0)
        {
            target = ((lastCommittedPercent_ + 4) / 5 - 1) * 5;
        }

        value = Clamp(target, kTrackMin, kTrackMax);
        SendMessageW(trackbar_, TBM_SETPOS, TRUE, value);
    }

    currentPercent_ = value;
    RepaintNameAndPercent();

    if (live)
    {
        return;
    }

    lastCommittedPercent_ = value;

    if (OnCommit)
    {
        OnCommit(value);
    }
}

LRESULT CustomValuePopup::HandleCustomDraw(NMHDR* header)
{
    auto* draw = reinterpret_cast<LPNMCUSTOMDRAW>(header);

    if (draw->dwDrawStage == CDDS_PREPAINT)
    {
        if (!loggedPrepaint_)
        {
            loggedPrepaint_ = true;
            Log(L"CustomValuePopup trackbar: CDDS_PREPAINT reached, returning CDRF_NOTIFYITEMDRAW.");
        }

        return CDRF_NOTIFYITEMDRAW;
    }

    if (draw->dwDrawStage == CDDS_ITEMPREPAINT)
    {
        if (draw->dwItemSpec == TBCD_CHANNEL)
        {
            if (!loggedChannel_)
            {
                loggedChannel_ = true;
                Log(L"CustomValuePopup trackbar: CDDS_ITEMPREPAINT/TBCD_CHANNEL reached, rc=(%ld,%ld,%ld,%ld) - "
                    L"drawing the dark channel and returning CDRF_SKIPDEFAULT.",
                    draw->rc.left, draw->rc.top, draw->rc.right, draw->rc.bottom);
            }

            RECT channel = draw->rc;
            const int mid = (channel.top + channel.bottom) / 2;
            channel.top = mid - 2;
            channel.bottom = mid + 2;

            FillRect(draw->hdc, &channel, channelBrush_);
            return CDRF_SKIPDEFAULT;
        }

        if (draw->dwItemSpec == TBCD_THUMB)
        {
            if (!loggedThumb_)
            {
                loggedThumb_ = true;
                Log(L"CustomValuePopup trackbar: CDDS_ITEMPREPAINT/TBCD_THUMB reached - drawing the thumb "
                    L"and returning CDRF_SKIPDEFAULT.");
            }

            HGDIOBJ oldBrush = SelectObject(draw->hdc, thumbBrush_);
            HGDIOBJ oldPen = SelectObject(draw->hdc, GetStockObject(NULL_PEN));
            Ellipse(draw->hdc, draw->rc.left, draw->rc.top, draw->rc.right, draw->rc.bottom);
            SelectObject(draw->hdc, oldBrush);
            SelectObject(draw->hdc, oldPen);
            return CDRF_SKIPDEFAULT;
        }

        if (draw->dwItemSpec == TBCD_TICS)
        {
            // comctl32 still sends a custom-draw notification for the tick-mark
            // part even with TBS_NOTICKS set; filled with the background color
            // directly rather than left to the stock control's default (white).
            if (!loggedChannelFallthrough_)
            {
                loggedChannelFallthrough_ = true;
                Log(L"CustomValuePopup trackbar: CDDS_ITEMPREPAINT/TBCD_TICS reached - filling it with "
                    L"the background color directly (skipping the draw entirely didn't clear the white "
                    L"fill on real hardware).");
            }

            RECT ticsRect = draw->rc;
            FillRect(draw->hdc, &ticsRect, backgroundBrush_);
            return CDRF_SKIPDEFAULT;
        }

        return CDRF_DODEFAULT;
    }

    // ITEMPREPAINT above already draws the entire visible surface by hand, so
    // these erase/post stages have nothing left to legitimately do.
    if (draw->dwDrawStage == CDDS_PREERASE || draw->dwDrawStage == CDDS_POSTERASE ||
        draw->dwDrawStage == CDDS_POSTPAINT || draw->dwDrawStage == CDDS_ITEMPREERASE ||
        draw->dwDrawStage == CDDS_ITEMPOSTERASE || draw->dwDrawStage == CDDS_ITEMPOSTPAINT)
    {
        if (!loggedEraseStage_)
        {
            loggedEraseStage_ = true;
            Log(L"CustomValuePopup trackbar: CDDS stage 0x%lX reached (PREERASE/POSTERASE/POSTPAINT "
                L"family) - skipping default, nothing left for it to draw.",
                static_cast<unsigned long>(draw->dwDrawStage));
        }

        return CDRF_SKIPDEFAULT;
    }

    return CDRF_DODEFAULT;
}

void CustomValuePopup::DrawNameAndPercent(HDC hdc)
{
    HFONT oldFont = (font_ != nullptr) ? static_cast<HFONT>(SelectObject(hdc, font_)) : nullptr;
    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, kText);

    wchar_t percentText[16];
    _snwprintf_s(percentText, _TRUNCATE, L"%d%%", currentPercent_);

    RECT nameRect{ 12, 8, kPopupWidth - 12, 32 };
    DrawTextW(hdc, L"All Monitors", -1, &nameRect, DT_LEFT | DT_NOCLIP);

    RECT percentRect{ 12, 8, kPopupWidth - 12, 32 };
    DrawTextW(hdc, percentText, -1, &percentRect, DT_RIGHT | DT_NOCLIP);

    if (oldFont != nullptr)
    {
        SelectObject(hdc, oldFont);
    }
}

void CustomValuePopup::RepaintNameAndPercent()
{
    HDC hdc = GetDC(window_);
    if (hdc == nullptr)
    {
        return;
    }

    RECT row{ 0, 0, kPopupWidth, 32 };
    FillRect(hdc, &row, backgroundBrush_);
    DrawNameAndPercent(hdc);

    ReleaseDC(window_, hdc);
}

void CustomValuePopup::DrawFooter(HDC hdc)
{
    const RECT area{ 12, 78, kPopupWidth - 12, kPopupHeight - 8 };

    HFONT oldFont = (font_ != nullptr) ? static_cast<HFONT>(SelectObject(hdc, font_)) : nullptr;
    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, kMuted);

    TEXTMETRICW metrics{};
    GetTextMetricsW(hdc, &metrics);
    const int lineHeight = metrics.tmHeight + metrics.tmExternalLeading;

    SIZE spaceSize{};
    GetTextExtentPoint32W(hdc, L" ", 1, &spaceSize);

    int x = area.left;
    int y = area.top;

    // Manual word-wrap: DrawText(DT_WORDBREAK) wraps text but doesn't report
    // where each word landed, which is needed to draw the "Ctrl" chip.
    const std::wstring text = kFooterText;
    size_t pos = 0;
    while (pos < text.size())
    {
        const size_t next = text.find(L' ', pos);
        const std::wstring word = (next == std::wstring::npos) ? text.substr(pos) : text.substr(pos, next - pos);
        pos = (next == std::wstring::npos) ? text.size() : next + 1;

        SIZE wordSize{};
        GetTextExtentPoint32W(hdc, word.c_str(), static_cast<int>(word.size()), &wordSize);

        const bool isCtrl = (word == L"Ctrl");
        const int leadingGap = isCtrl ? kCtrlExtraGap : 0;

        if (x != area.left && x + leadingGap + wordSize.cx > area.right)
        {
            x = area.left;
            y += lineHeight;
        }
        else
        {
            x += leadingGap;
        }

        if (isCtrl)
        {
            const RECT chip{ x - 3, y, x + wordSize.cx + 3, y + lineHeight - 2 };

            HBRUSH chipBrush = CreateSolidBrush(kChipFill);
            HGDIOBJ oldBrush = SelectObject(hdc, chipBrush);
            HPEN chipPen = CreatePen(PS_SOLID, 1, kBorder);
            HGDIOBJ oldPen = SelectObject(hdc, chipPen);

            RoundRect(hdc, chip.left, chip.top, chip.right, chip.bottom, 4, 4);

            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);
            DeleteObject(chipBrush);
            DeleteObject(chipPen);
        }

        TextOutW(hdc, x, y, word.c_str(), static_cast<int>(word.size()));
        x += wordSize.cx + spaceSize.cx + leadingGap;
    }

    if (oldFont != nullptr)
    {
        SelectObject(hdc, oldFont);
    }
}

LRESULT CALLBACK CustomValuePopup::WindowProcThunk(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    CustomValuePopup* self = nullptr;

    if (message == WM_NCCREATE)
    {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        self = static_cast<CustomValuePopup*>(create->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    else
    {
        self = reinterpret_cast<CustomValuePopup*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    }

    if (self == nullptr)
    {
        // `window`, not self->window_, which is still null this early.
        return DefWindowProcW(window, message, wParam, lParam);
    }

    return self->HandleMessage(window, message, wParam, lParam);
}
