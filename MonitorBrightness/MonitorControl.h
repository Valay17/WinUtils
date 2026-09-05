#pragma once

#include <windows.h>

#include <string>
#include <vector>

#include "Config.h"

struct MonitorInfo
{
    HANDLE handle = nullptr;
    HMONITOR display = nullptr;
    std::wstring description;
    std::wstring id;
    std::wstring label;
    int displayNumber = 0;

    bool supported = false;

    DWORD minimum = 0;
    DWORD maximum = 100;
    DWORD current = 0;

    int Percent() const;
};

class MonitorControl
{
public:
    ~MonitorControl();

    int Refresh();

    void RefreshAsync(HWND notifyWindow, UINT notifyMessage);
    int ApplyPendingRefresh();

    const std::vector<MonitorInfo>& Monitors() const { return monitors_; }

    bool Any() const { return !monitors_.empty(); }
    int SupportedCount() const;

    bool SetPercent(size_t index, int percent);

    void SetPercentAllAsync(int percent, HWND notifyWindow, UINT notifyMessage);
    void ApplyPendingSetPercentAll();

    int AdjustPercent(size_t index, int deltaPercent);

#if MONITORBRIGHTNESS_ENABLE_HOTKEYS
    int IndexUnderCursor() const;
#endif

    void Release();

private:
    static std::vector<MonitorInfo> EnumerateAndProbe();
    static DWORD WINAPI RefreshThreadProc(LPVOID param);
    static DWORD WINAPI SetPercentAllThreadProc(LPVOID param);

    std::vector<MonitorInfo> monitors_;

    bool refreshInProgress_ = false;
    std::vector<MonitorInfo> pendingRefresh_;

    struct PendingBrightness
    {
        size_t index;
        DWORD raw;
        bool ok;
    };

    bool setPercentAllInProgress_ = false;
    std::vector<PendingBrightness> pendingSetPercentAll_;

    bool hasQueuedPercentAll_ = false;
    int queuedPercentAll_ = 0;
    HWND queuedNotifyWindow_ = nullptr;
    UINT queuedNotifyMessage_ = 0;
};
