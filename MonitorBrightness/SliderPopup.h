#pragma once

#include <windows.h>

#include <functional>
#include <vector>

#include "MonitorControl.h"

class SliderPopup
{
public:
    bool Create(HINSTANCE instance);
    void Show(MonitorControl& monitors, POINT anchor);
    void Hide();
    bool Visible() const { return visible_; }
    void Destroy();

    std::function<void()> OnCommitted;

private:
    struct Row
    {
        size_t monitorIndex = 0;
        HWND nameLabel = nullptr;
        HWND trackbar = nullptr;
        HWND percentLabel = nullptr;
        int lastCommittedPercent = 0;
        bool dragging = false;
    };

    static LRESULT CALLBACK WindowProcThunk(HWND window, UINT message, WPARAM wParam, LPARAM lParam);
    static LRESULT CALLBACK TrackbarSubclassProc(
        HWND trackbar, UINT message, WPARAM wParam, LPARAM lParam,
        UINT_PTR subclassId, DWORD_PTR refData);

    void FocusAdjacentRow(HWND from, bool backward);

    LRESULT HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam);

    void BuildRows(MonitorControl& monitors);
    void DestroyRows();
    void Position(POINT anchor, int height);
    void HandleScroll(HWND trackbar, int code, int rawValue);
    void SetPercentLabel(HWND label, int percent);
    LRESULT HandleCustomDraw(NMHDR* header);
    Row* RowForTrackbar(HWND trackbar);
    void DrawFooter(HDC hdc);
    void ShowEmptyMessage(POINT anchor);

    HINSTANCE instance_ = nullptr;
    HWND window_ = nullptr;
    HWND emptyLabel_ = nullptr;
    HFONT font_ = nullptr;
    HBRUSH backgroundBrush_ = nullptr;
    MonitorControl* monitors_ = nullptr;
    std::vector<Row> rows_;
    bool visible_ = false;

    int footerTop_ = 0;
};
