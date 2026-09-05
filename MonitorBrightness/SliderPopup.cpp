#include "SliderPopup.h"

#include <commctrl.h>

#include <algorithm>
#include <string>

#include "Config.h"

namespace
{

constexpr wchar_t kPopupClass[] = L"MonitorBrightness_SliderPopup";

constexpr int kPopupWidth = 260;
constexpr int kEdgePadding = 12;
constexpr int kScreenMargin = 8;
constexpr int kTopPadding = 10;

constexpr int kRowNameTop = 0;
constexpr int kRowNameHeight = 18;
constexpr int kRowTrackbarTop = 20;
constexpr int kRowTrackbarHeight = 24;
constexpr int kRowHeight = kRowTrackbarTop + kRowTrackbarHeight + 8;

constexpr int kFooterHeight = 50;
constexpr wchar_t kFooterText[] = L"Hold Ctrl to Snap to Nearest 5%.";

constexpr UINT_PTR kTrackbarSubclassId = 1;
constexpr int kPercentLabelWidth = 44;

constexpr COLORREF kBackground = RGB(0x2b, 0x2b, 0x2b);
constexpr COLORREF kBorder = RGB(80, 80, 80);
constexpr COLORREF kText = RGB(240, 240, 240);
constexpr COLORREF kMuted = RGB(180, 180, 180);
constexpr COLORREF kChannel = RGB(60, 60, 60);
constexpr COLORREF kAccent = RGB(0x00, 0x78, 0xd4);
constexpr COLORREF kChipFill = RGB(0x3a, 0x3a, 0x3a);

constexpr int kCtrlExtraGap = 4;

int Clamp(int value, int low, int high)
{
    return value < low ? low : (value > high ? high : value);
}

int SnapTo5(int percent)
{
    return Clamp(((percent + 2) / 5) * 5, 0, 100);
}

} // namespace

bool SliderPopup::Create(HINSTANCE instance)
{
    instance_ = instance;

    INITCOMMONCONTROLSEX icc{};
    icc.dwSize = sizeof(icc);
    icc.dwICC = ICC_BAR_CLASSES;
    if (!InitCommonControlsEx(&icc))
    {
        Log(L"SliderPopup: InitCommonControlsEx failed (Win32 %lu) - continuing anyway, "
            L"the trackbar class may already be registered regardless.", GetLastError());
    }

    backgroundBrush_ = CreateSolidBrush(kBackground);
    if (backgroundBrush_ == nullptr)
    {
        Log(L"SliderPopup: CreateSolidBrush failed (Win32 %lu).", GetLastError());
    }

    NONCLIENTMETRICSW metrics{};
    metrics.cbSize = sizeof(metrics);
    if (SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0))
    {
        font_ = CreateFontIndirectW(&metrics.lfMessageFont);
        if (font_ == nullptr)
        {
            Log(L"SliderPopup: CreateFontIndirectW failed (Win32 %lu) - labels will use "
                L"whatever default font their control falls back to.", GetLastError());
        }
    }
    else
    {
        Log(L"SliderPopup: SystemParametersInfoW(SPI_GETNONCLIENTMETRICS) failed (Win32 %lu).",
            GetLastError());
    }

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = &SliderPopup::WindowProcThunk;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = kPopupClass;
    windowClass.hbrBackground = backgroundBrush_;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);

    if (RegisterClassExW(&windowClass) == 0)
    {
        Log(L"SliderPopup: RegisterClassExW failed (Win32 %lu).", GetLastError());
        return false;
    }

    window_ = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_TOPMOST, kPopupClass, L"Monitor Brightness",
        WS_POPUP, 0, 0, kPopupWidth, kTopPadding + kFooterHeight,
        nullptr, nullptr, instance, this);

    if (window_ == nullptr)
    {
        Log(L"SliderPopup: CreateWindowExW (main popup window) failed (Win32 %lu).", GetLastError());
        return false;
    }

    Log(L"SliderPopup: created OK.");
    return true;
}

void SliderPopup::Destroy()
{
    if (window_ != nullptr)
    {
        DestroyWindow(window_);
        window_ = nullptr;
    }

    if (!UnregisterClassW(kPopupClass, instance_))
    {
        Log(L"SliderPopup: UnregisterClassW failed (Win32 %lu).", GetLastError());
    }
}

void SliderPopup::BuildRows(MonitorControl& monitors)
{
    DestroyRows();

    const auto& list = monitors.Monitors();
    for (size_t i = 0; i < list.size(); ++i)
    {
        if (!list[i].supported)
        {
            continue;
        }

        Row row;
        row.monitorIndex = i;
        row.lastCommittedPercent = list[i].Percent();

        const int top = kTopPadding + static_cast<int>(rows_.size()) * kRowHeight;

        row.nameLabel = CreateWindowExW(
            0, L"STATIC", list[i].label.c_str(), WS_CHILD | WS_VISIBLE | SS_LEFT,
            kEdgePadding, top + kRowNameTop, kPopupWidth - kEdgePadding - kPercentLabelWidth - kEdgePadding,
            kRowNameHeight, window_, nullptr, instance_, nullptr);

        row.percentLabel = CreateWindowExW(
            0, L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_RIGHT,
            kPopupWidth - kEdgePadding - kPercentLabelWidth, top + kRowNameTop,
            kPercentLabelWidth, kRowNameHeight, window_, nullptr, instance_, nullptr);

        row.trackbar = CreateWindowExW(
            0, TRACKBAR_CLASSW, L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | TBS_HORZ | TBS_NOTICKS | TBS_TRANSPARENTBKGND,
            kEdgePadding, top + kRowTrackbarTop, kPopupWidth - kEdgePadding * 2, kRowTrackbarHeight,
            window_, nullptr, instance_, nullptr);

        if (row.trackbar != nullptr)
        {
            SendMessageW(row.trackbar, TBM_SETRANGE, TRUE, MAKELPARAM(0, 100));
            SendMessageW(row.trackbar, TBM_SETPOS, TRUE, list[i].Percent());

            SetWindowSubclass(row.trackbar, &SliderPopup::TrackbarSubclassProc,
                               kTrackbarSubclassId, reinterpret_cast<DWORD_PTR>(this));
        }
        else
        {
            Log(L"SliderPopup: CreateWindowExW (trackbar, %s) failed (Win32 %lu).",
                list[i].label.c_str(), GetLastError());
        }

        if (font_ != nullptr)
        {
            if (row.nameLabel != nullptr)
            {
                SendMessageW(row.nameLabel, WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
            }

            if (row.percentLabel != nullptr)
            {
                SendMessageW(row.percentLabel, WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
            }
        }

        SetPercentLabel(row.percentLabel, list[i].Percent());
        rows_.push_back(row);
    }
}

void SliderPopup::DestroyRows()
{
    for (auto& row : rows_)
    {
        if (row.nameLabel != nullptr) DestroyWindow(row.nameLabel);
        if (row.percentLabel != nullptr) DestroyWindow(row.percentLabel);
        if (row.trackbar != nullptr) DestroyWindow(row.trackbar);
    }

    rows_.clear();
}

void SliderPopup::Show(MonitorControl& monitors, POINT anchor)
{
    if (window_ == nullptr)
    {
        return;
    }

    monitors_ = &monitors;
    BuildRows(monitors);

    if (rows_.empty())
    {
        ShowEmptyMessage(anchor);
        return;
    }

    if (emptyLabel_ != nullptr)
    {
        ShowWindow(emptyLabel_, SW_HIDE);
    }

    const int contentHeight = kTopPadding
        + static_cast<int>(rows_.size()) * kRowHeight
        + kFooterHeight;

    footerTop_ = contentHeight - kFooterHeight + 4;

    Position(anchor, contentHeight);
    ShowWindow(window_, SW_SHOWNOACTIVATE);
    visible_ = true;

    SetForegroundWindow(window_);

    if (!rows_.empty() && rows_[0].trackbar != nullptr)
    {
        SetFocus(rows_[0].trackbar);
    }
}

void SliderPopup::Hide()
{
    if (!visible_)
    {
        return;
    }

    visible_ = false;
    ShowWindow(window_, SW_HIDE);
    DestroyRows();

    if (emptyLabel_ != nullptr)
    {
        ShowWindow(emptyLabel_, SW_HIDE);
    }
}

void SliderPopup::Position(POINT anchor, int height)
{
    RECT work{ 0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN) };

    const HMONITOR monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    if (monitor != nullptr && GetMonitorInfoW(monitor, &info))
    {
        work = info.rcWork;
    }

    int x = anchor.x - kPopupWidth / 2;
    int y = anchor.y - height - kScreenMargin;

    x = Clamp(x, work.left + kScreenMargin, std::max<int>(work.left + kScreenMargin, work.right - kPopupWidth - kScreenMargin));
    y = Clamp(y, work.top + kScreenMargin, std::max<int>(work.top + kScreenMargin, work.bottom - height - kScreenMargin));

    SetWindowPos(window_, HWND_TOPMOST, x, y, kPopupWidth, height, SWP_NOACTIVATE);
}

void SliderPopup::SetPercentLabel(HWND label, int percent)
{
    if (label == nullptr)
    {
        return;
    }

    wchar_t text[16];
    _snwprintf_s(text, _TRUNCATE, L"%d%%", percent);
    SetWindowTextW(label, text);
}

void SliderPopup::DrawFooter(HDC hdc)
{
    if (rows_.empty())
    {
        return;
    }

    const RECT area{ kEdgePadding, footerTop_, kPopupWidth - kEdgePadding, footerTop_ + kFooterHeight };

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

void SliderPopup::ShowEmptyMessage(POINT anchor)
{
    if (window_ == nullptr)
    {
        return;
    }

    if (emptyLabel_ == nullptr)
    {
        emptyLabel_ = CreateWindowExW(
            0, L"STATIC", L"No Controllable Monitors Detected (Try Re-detect Monitors)",
            WS_CHILD | SS_CENTER, kEdgePadding, kTopPadding,
            kPopupWidth - kEdgePadding * 2, kFooterHeight, window_, nullptr, instance_, nullptr);

        if (emptyLabel_ == nullptr)
        {
            Log(L"SliderPopup: CreateWindowExW (empty-state label) failed (Win32 %lu).", GetLastError());
        }
        else if (font_ != nullptr)
        {
            SendMessageW(emptyLabel_, WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
        }
    }

    if (emptyLabel_ == nullptr)
    {
        return;
    }

    ShowWindow(emptyLabel_, SW_SHOWNOACTIVATE);

    const int contentHeight = kTopPadding + kFooterHeight + kTopPadding;
    Position(anchor, contentHeight);
    ShowWindow(window_, SW_SHOWNOACTIVATE);
    visible_ = true;
    SetForegroundWindow(window_);
}

SliderPopup::Row* SliderPopup::RowForTrackbar(HWND trackbar)
{
    for (auto& row : rows_)
    {
        if (row.trackbar == trackbar)
        {
            return &row;
        }
    }

    return nullptr;
}

void SliderPopup::HandleScroll(HWND trackbar, int code, int rawValue)
{
    Row* row = RowForTrackbar(trackbar);
    if (row == nullptr || monitors_ == nullptr)
    {
        return;
    }

    // TB_THUMBTRACK/TB_THUMBPOSITION carry the live position in the
    // notification itself; every other code reads it back from the control.
    const bool live = (code == TB_THUMBTRACK || code == TB_THUMBPOSITION);
    const bool snap = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    // TB_TOP/TB_BOTTOM (Home/End) jump straight to 0/100 and are already
    // 5-aligned, so the keyboard-nudge snap logic below skips them.
    const bool absoluteJump = (code == TB_TOP || code == TB_BOTTOM);

    int value = live ? rawValue : static_cast<int>(SendMessageW(trackbar, TBM_GETPOS, 0, 0));

    if (live)
    {
        row->dragging = true;

        if (snap)
        {
            value = SnapTo5(value);
            SendMessageW(trackbar, TBM_SETPOS, TRUE, value);
        }
    }
    else if (row->dragging)
    {
        row->dragging = false;
        value = static_cast<int>(SendMessageW(trackbar, TBM_GETPOS, 0, 0));
    }
    else if (snap && !absoluteJump)
    {
        // Steps a clean +-5 from the last committed value rather than the
        // trackbar's already-nudged raw position, using direction-aware
        // floor/ceil so a value that isn't itself a multiple of 5 doesn't
        // get part of the step silently eaten by rounding first.
        const int direction = (value > row->lastCommittedPercent) ? 1
                             : (value < row->lastCommittedPercent) ? -1 : 0;

        int target = row->lastCommittedPercent;
        if (direction > 0)
        {
            target = (row->lastCommittedPercent / 5 + 1) * 5;
        }
        else if (direction < 0)
        {
            target = ((row->lastCommittedPercent + 4) / 5 - 1) * 5;
        }

        value = Clamp(target, 0, 100);
        SendMessageW(trackbar, TBM_SETPOS, TRUE, value);
    }

    SetPercentLabel(row->percentLabel, value);

    if (live)
    {
        return;
    }

    row->lastCommittedPercent = value;

    if (monitors_->SetPercent(row->monitorIndex, value) && OnCommitted)
    {
        OnCommitted();
    }
}

LRESULT SliderPopup::HandleCustomDraw(NMHDR* header)
{
    auto* draw = reinterpret_cast<LPNMCUSTOMDRAW>(header);

    switch (draw->dwDrawStage)
    {
    case CDDS_PREPAINT:
        return CDRF_NOTIFYITEMDRAW;

    case CDDS_ITEMPREPAINT:
    {
        // dwItemSpec carries which part (TBCD_CHANNEL/TBCD_THUMB) is about to draw.
        if (draw->dwItemSpec == TBCD_CHANNEL)
        {
            RECT channel = draw->rc;
            const int mid = (channel.top + channel.bottom) / 2;
            channel.top = mid - 2;
            channel.bottom = mid + 2;

            HBRUSH channelBrush = CreateSolidBrush(kChannel);
            FillRect(draw->hdc, &channel, channelBrush);
            DeleteObject(channelBrush);
            return CDRF_SKIPDEFAULT;
        }

        if (draw->dwItemSpec == TBCD_THUMB)
        {
            HBRUSH thumbBrush = CreateSolidBrush(kAccent);
            HGDIOBJ oldBrush = SelectObject(draw->hdc, thumbBrush);
            HGDIOBJ oldPen = SelectObject(draw->hdc, GetStockObject(NULL_PEN));
            Ellipse(draw->hdc, draw->rc.left, draw->rc.top, draw->rc.right, draw->rc.bottom);
            SelectObject(draw->hdc, oldBrush);
            SelectObject(draw->hdc, oldPen);
            DeleteObject(thumbBrush);
            return CDRF_SKIPDEFAULT;
        }

        return CDRF_DODEFAULT;
    }

    default:
        return CDRF_DODEFAULT;
    }
}

LRESULT SliderPopup::HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message)
    {
    case WM_HSCROLL:
        HandleScroll(reinterpret_cast<HWND>(lParam), LOWORD(wParam), HIWORD(wParam));
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

    case WM_CTLCOLORSTATIC:
    {
        HDC hdc = reinterpret_cast<HDC>(wParam);
        SetTextColor(hdc, kText);
        SetBkMode(hdc, TRANSPARENT);
        return reinterpret_cast<LRESULT>(backgroundBrush_);
    }

    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(window, &ps);
        RECT client;
        GetClientRect(window, &client);
        HBRUSH borderBrush = CreateSolidBrush(kBorder);
        FrameRect(hdc, &client, borderBrush);
        DeleteObject(borderBrush);
        DrawFooter(hdc);
        EndPaint(window, &ps);
        return 0;
    }

    case WM_ACTIVATE:
        // WA_INACTIVE: this popup just lost activation, i.e. the user clicked elsewhere.
        if (LOWORD(wParam) == WA_INACTIVE && visible_)
        {
            Hide();
        }
        return 0;

    case WM_KEYDOWN:
        if (wParam == VK_ESCAPE)
        {
            Hide();
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);

    case WM_DESTROY:
        if (backgroundBrush_ != nullptr) DeleteObject(backgroundBrush_);
        if (font_ != nullptr) DeleteObject(font_);
        return 0;

    default:
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

void SliderPopup::FocusAdjacentRow(HWND from, bool backward)
{
    if (rows_.empty())
    {
        return;
    }

    size_t index = 0;
    for (; index < rows_.size(); ++index)
    {
        if (rows_[index].trackbar == from)
        {
            break;
        }
    }

    if (index >= rows_.size())
    {
        return;
    }

    const size_t count = rows_.size();
    const size_t next = backward ? (index + count - 1) % count : (index + 1) % count;

    if (rows_[next].trackbar != nullptr)
    {
        SetFocus(rows_[next].trackbar);
    }
}

LRESULT CALLBACK SliderPopup::TrackbarSubclassProc(
    HWND trackbar, UINT message, WPARAM wParam, LPARAM lParam,
    UINT_PTR /*subclassId*/, DWORD_PTR refData)
{
    if (message == WM_KEYDOWN)
    {
        auto* self = reinterpret_cast<SliderPopup*>(refData);

        if (wParam == VK_ESCAPE)
        {
            self->Hide();
            return 0;
        }

        if (wParam == VK_TAB)
        {
            self->FocusAdjacentRow(trackbar, (GetKeyState(VK_SHIFT) & 0x8000) != 0);
            return 0;
        }

        if (wParam == VK_UP || wParam == VK_DOWN)
        {
            self->FocusAdjacentRow(trackbar, wParam == VK_UP);
            return 0;
        }
    }

    return DefSubclassProc(trackbar, message, wParam, lParam);
}

// Subclasses are removed automatically on WM_NCDESTROY, so DestroyRows()
// needs no matching RemoveWindowSubclass call.

LRESULT CALLBACK SliderPopup::WindowProcThunk(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    SliderPopup* self = nullptr;

    if (message == WM_NCCREATE)
    {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        self = static_cast<SliderPopup*>(create->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    else
    {
        self = reinterpret_cast<SliderPopup*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    }

    if (self == nullptr)
    {
        return DefWindowProcW(window, message, wParam, lParam);
    }

    // Uses `window`, the real handle from the OS, not self->window_ - which
    // is not yet assigned for messages sent synchronously during CreateWindowExW.
    return self->HandleMessage(window, message, wParam, lParam);
}
