#include "MonitorControl.h"

#include <highlevelmonitorconfigurationapi.h>
#include <physicalmonitorenumerationapi.h>

#include <algorithm>
#include <memory>
#include <utility>

#include "Config.h"

namespace
{

struct EnumContext
{
    std::vector<HMONITOR> displays;
};

BOOL CALLBACK CollectDisplay(HMONITOR monitor, HDC, LPRECT, LPARAM param)
{
    auto* context = reinterpret_cast<EnumContext*>(param);
    context->displays.push_back(monitor);
    return TRUE;
}

int Clamp(int value, int low, int high)
{
    return value < low ? low : (value > high ? high : value);
}

struct ProbeThreadContext
{
    MonitorInfo* info;
    DWORD* error;
};

DWORD WINAPI ProbeThreadProc(LPVOID param)
{
    auto* context = static_cast<ProbeThreadContext*>(param);
    MonitorInfo& info = *context->info;

    DWORD minimum = 0;
    DWORD current = 0;
    DWORD maximum = 0;

    if (GetMonitorBrightness(info.handle, &minimum, &current, &maximum) &&
        maximum > minimum)
    {
        info.supported = true;
        info.minimum = minimum;
        info.current = current;
        info.maximum = maximum;
    }
    else
    {
        info.supported = false;
        *context->error = GetLastError();
    }

    return 0;
}

bool SameSource(const DISPLAYCONFIG_PATH_SOURCE_INFO& a, const DISPLAYCONFIG_PATH_SOURCE_INFO& b)
{
    return a.id == b.id &&
           a.adapterId.LowPart == b.adapterId.LowPart &&
           a.adapterId.HighPart == b.adapterId.HighPart;
}

bool TryParseDisplayNumber(const std::wstring& deviceName, int& number)
{
    static const wchar_t* const prefix = L"\\\\.\\DISPLAY";
    static const size_t prefixLen = wcslen(prefix);

    if (deviceName.compare(0, prefixLen, prefix) != 0)
    {
        return false;
    }

    wchar_t* end = nullptr;
    const long value = wcstol(deviceName.c_str() + prefixLen, &end, 10);
    if (end == deviceName.c_str() + prefixLen || *end != L'\0' || value <= 0)
    {
        return false;
    }

    number = static_cast<int>(value);
    return true;
}

std::vector<std::wstring> InternalPanelDeviceNames()
{
    std::vector<std::wstring> result;

    UINT32 pathCount = 0;
    UINT32 modeCount = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != ERROR_SUCCESS)
    {
        return result;
    }

    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(),
                            &modeCount, modes.data(), nullptr) != ERROR_SUCCESS)
    {
        return result;
    }

    std::vector<bool> ambiguous(pathCount, false);
    for (UINT32 i = 0; i < pathCount; ++i)
    {
        for (UINT32 j = i + 1; j < pathCount; ++j)
        {
            if (SameSource(paths[i].sourceInfo, paths[j].sourceInfo))
            {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }

    for (UINT32 i = 0; i < pathCount; ++i)
    {
        const DISPLAYCONFIG_PATH_INFO& path = paths[i];
        if (path.targetInfo.outputTechnology != DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL)
        {
            continue;
        }

        if (ambiguous[i])
        {
            Log(L"InternalPanelDeviceNames: path %u shares its source with another active path - looks "
                L"like Duplicate display mode. Skipping internal-panel detection for just this one rather "
                L"than every monitor.", i);
            continue;
        }

        DISPLAYCONFIG_SOURCE_DEVICE_NAME sourceName{};
        sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        sourceName.header.size = sizeof(sourceName);
        sourceName.header.adapterId = path.sourceInfo.adapterId;
        sourceName.header.id = path.sourceInfo.id;

        if (DisplayConfigGetDeviceInfo(&sourceName.header) == ERROR_SUCCESS)
        {
            result.emplace_back(sourceName.viewGdiDeviceName);
        }
    }

    return result;
}

void FillLabel(MonitorInfo& info, const std::vector<std::wstring>& internalPanels)
{
    MONITORINFOEXW monitorInfo{};
    monitorInfo.cbSize = sizeof(monitorInfo);

    std::wstring deviceName;
    if (info.display != nullptr && GetMonitorInfoW(info.display, &monitorInfo))
    {
        deviceName = monitorInfo.szDevice;
    }

    int number = 0;
    if (!deviceName.empty() && TryParseDisplayNumber(deviceName, number))
    {
        info.displayNumber = number;
    }

    const bool isInternal = !deviceName.empty() &&
        std::find(internalPanels.begin(), internalPanels.end(), deviceName) != internalPanels.end();

    if (isInternal)
    {
        info.label = L"Internal Display";
    }
    else if (info.displayNumber > 0)
    {
        info.label = L"Monitor " + std::to_wstring(info.displayNumber);
    }
    else if (!deviceName.empty())
    {
        info.label = deviceName;
    }
    else
    {
        info.label = L"Display";
    }
}

} // namespace

int MonitorInfo::Percent() const
{
    if (!supported || maximum <= minimum)
    {
        return 0;
    }

    const double span = static_cast<double>(maximum - minimum);
    const double offset = static_cast<double>(current - minimum);
    return Clamp(static_cast<int>((offset / span) * 100.0 + 0.5), 0, 100);
}

MonitorControl::~MonitorControl()
{
    Release();
}

void MonitorControl::Release()
{
    for (auto& monitor : monitors_)
    {
        if (monitor.handle != nullptr)
        {
            PHYSICAL_MONITOR physical{};
            physical.hPhysicalMonitor = monitor.handle;
            DestroyPhysicalMonitors(1, &physical);
            monitor.handle = nullptr;
        }
    }

    monitors_.clear();
}

std::vector<MonitorInfo> MonitorControl::EnumerateAndProbe()
{
    std::vector<MonitorInfo> result;

    EnumContext context;
    if (!EnumDisplayMonitors(nullptr, nullptr, &CollectDisplay,
                             reinterpret_cast<LPARAM>(&context)))
    {
        Log(L"EnumDisplayMonitors failed (Win32 %lu).", GetLastError());
        return result;
    }

    Log(L"--- monitor enumeration: %zu display(s) ---", context.displays.size());

    const std::vector<std::wstring> internalPanels = InternalPanelDeviceNames();

    for (HMONITOR display : context.displays)
    {
        DWORD count = 0;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(display, &count) || count == 0)
        {
            Log(L"  a display reported no physical monitors (Win32 %lu) - skipped.",
                GetLastError());
            continue;
        }

        std::vector<PHYSICAL_MONITOR> physical(count);
        if (!GetPhysicalMonitorsFromHMONITOR(display, count, physical.data()))
        {
            Log(L"  GetPhysicalMonitorsFromHMONITOR failed (Win32 %lu) - skipped.",
                GetLastError());
            continue;
        }

        for (DWORD i = 0; i < count; ++i)
        {
            MonitorInfo info;
            info.handle = physical[i].hPhysicalMonitor;
            info.display = display;
            info.description = physical[i].szPhysicalMonitorDescription;
            FillLabel(info, internalPanels);
            info.id = info.description + L"#" + std::to_wstring(result.size());

            result.push_back(info);
        }
    }

    std::vector<DWORD> probeErrors(result.size(), 0);
    std::vector<ProbeThreadContext> contexts(result.size());
    std::vector<HANDLE> threads(result.size(), nullptr);

    for (size_t i = 0; i < result.size(); ++i)
    {
        contexts[i] = ProbeThreadContext{ &result[i], &probeErrors[i] };
        threads[i] = CreateThread(nullptr, 0, &ProbeThreadProc, &contexts[i], 0, nullptr);
        if (threads[i] == nullptr)
        {
            Log(L"  EnumerateAndProbe: CreateThread failed for monitor %zu (Win32 %lu) - "
                L"probing it on this thread instead.", i, GetLastError());
            ProbeThreadProc(&contexts[i]);
        }
    }

    for (HANDLE thread : threads)
    {
        if (thread != nullptr)
        {
            WaitForSingleObject(thread, INFINITE);
            CloseHandle(thread);
        }
    }

    for (size_t i = 0; i < result.size(); ++i)
    {
        const MonitorInfo& info = result[i];
        if (info.supported)
        {
            Log(L"  [%zu] %s - DDC/CI OK, range %lu-%lu, currently %lu (%d%%)",
                i, info.description.c_str(),
                info.minimum, info.maximum, info.current, info.Percent());
        }
        else
        {
            Log(L"  [%zu] %s - no DDC/CI (Win32 %lu). Either the monitor does not "
                L"support it, it is disabled in the monitor's OSD menu, or this is "
                L"the internal laptop panel, which Windows controls itself.",
                i, info.description.c_str(), probeErrors[i]);
        }
    }

    std::stable_sort(result.begin(), result.end(),
                      [](const MonitorInfo& a, const MonitorInfo& b) {
                          return a.displayNumber < b.displayNumber;
                      });

    return result;
}

int MonitorControl::Refresh()
{
    Release();
    monitors_ = EnumerateAndProbe();

    const int supported = SupportedCount();
    Log(L"Enumeration complete: %zu monitor(s), %d controllable.",
        monitors_.size(), supported);

    return supported;
}

namespace
{

struct RefreshThreadContext
{
    MonitorControl* self;
    HWND notifyWindow;
    UINT notifyMessage;
};

} // namespace

DWORD WINAPI MonitorControl::RefreshThreadProc(LPVOID param)
{
    std::unique_ptr<RefreshThreadContext> context(static_cast<RefreshThreadContext*>(param));

    context->self->pendingRefresh_ = EnumerateAndProbe();
    PostMessageW(context->notifyWindow, context->notifyMessage, 0, 0);
    return 0;
}

void MonitorControl::RefreshAsync(HWND notifyWindow, UINT notifyMessage)
{
    if (refreshInProgress_)
    {
        Log(L"MonitorControl::RefreshAsync: already in progress - ignoring the overlapping request.");
        return;
    }

    if (setPercentAllInProgress_)
    {
        Log(L"MonitorControl::RefreshAsync: a Set Value For All commit is still in flight - "
            L"ignoring this refresh request rather than risk destroying its handles mid-write.");
        return;
    }

    refreshInProgress_ = true;

    auto* context = new RefreshThreadContext{ this, notifyWindow, notifyMessage };
    const HANDLE thread = CreateThread(nullptr, 0, &RefreshThreadProc, context, 0, nullptr);

    if (thread == nullptr)
    {
        Log(L"MonitorControl::RefreshAsync: CreateThread failed (Win32 %lu) - falling back to a "
            L"synchronous enumeration.", GetLastError());
        delete context;

        pendingRefresh_ = EnumerateAndProbe();
        PostMessageW(notifyWindow, notifyMessage, 0, 0);
        return;
    }

    CloseHandle(thread); // detached - RefreshThreadProc frees its own context
}

int MonitorControl::ApplyPendingRefresh()
{
    Release();
    monitors_ = std::move(pendingRefresh_);
    pendingRefresh_.clear();
    refreshInProgress_ = false;

    const int supported = SupportedCount();
    Log(L"Enumeration complete (async): %zu monitor(s), %d controllable.",
        monitors_.size(), supported);

    if (hasQueuedPercentAll_)
    {
        hasQueuedPercentAll_ = false;
        SetPercentAllAsync(queuedPercentAll_, queuedNotifyWindow_, queuedNotifyMessage_);
    }

    return supported;
}

int MonitorControl::SupportedCount() const
{
    return static_cast<int>(
        std::count_if(monitors_.begin(), monitors_.end(),
                      [](const MonitorInfo& m) { return m.supported; }));
}

bool MonitorControl::SetPercent(size_t index, int percent)
{
    if (index >= monitors_.size())
    {
        return false;
    }

    MonitorInfo& monitor = monitors_[index];
    if (!monitor.supported)
    {
        return false;
    }

    percent = Clamp(percent, 0, 100);

    const double span = static_cast<double>(monitor.maximum - monitor.minimum);
    const DWORD raw = monitor.minimum +
        static_cast<DWORD>((span * percent / 100.0) + 0.5);

    if (!SetMonitorBrightness(monitor.handle, raw))
    {
        Log(L"SetMonitorBrightness failed for %s (Win32 %lu).",
            monitor.description.c_str(), GetLastError());
        return false;
    }

    monitor.current = raw;
    Log(L"%s -> %d%% (raw %lu)", monitor.description.c_str(), percent, raw);
    return true;
}

namespace
{

struct SetPercentAllThreadContext
{
    MonitorControl* self;
    HWND notifyWindow;
    UINT notifyMessage;

    std::vector<std::pair<size_t, HANDLE>> targets; // monitors_ index, physical handle
    std::vector<DWORD> rawTargets;                  // parallel to targets
};

} // namespace

void MonitorControl::SetPercentAllAsync(int percent, HWND notifyWindow, UINT notifyMessage)
{
    if (setPercentAllInProgress_ || refreshInProgress_)
    {
        Log(L"MonitorControl::SetPercentAllAsync: %s in progress - queuing this request "
            L"(%d%%) to run once it clears.",
            setPercentAllInProgress_ ? L"already" : L"a refresh is", percent);
        hasQueuedPercentAll_ = true;
        queuedPercentAll_ = percent;
        queuedNotifyWindow_ = notifyWindow;
        queuedNotifyMessage_ = notifyMessage;
        return;
    }

    percent = Clamp(percent, 0, 100);

    auto context = std::make_unique<SetPercentAllThreadContext>();
    context->self = this;
    context->notifyWindow = notifyWindow;
    context->notifyMessage = notifyMessage;

    for (size_t i = 0; i < monitors_.size(); ++i)
    {
        const MonitorInfo& monitor = monitors_[i];
        if (!monitor.supported)
        {
            continue;
        }

        const double span = static_cast<double>(monitor.maximum - monitor.minimum);
        const DWORD raw = monitor.minimum +
            static_cast<DWORD>((span * percent / 100.0) + 0.5);

        context->targets.emplace_back(i, monitor.handle);
        context->rawTargets.push_back(raw);
    }

    if (context->targets.empty())
    {
        return;
    }

    setPercentAllInProgress_ = true;

    SetPercentAllThreadContext* raw = context.release();
    const HANDLE thread = CreateThread(nullptr, 0, &SetPercentAllThreadProc, raw, 0, nullptr);

    if (thread == nullptr)
    {
        Log(L"MonitorControl::SetPercentAllAsync: CreateThread failed (Win32 %lu) - falling back to "
            L"synchronous.", GetLastError());
        setPercentAllInProgress_ = false;

        for (const auto& target : raw->targets)
        {
            SetPercent(target.first, percent);
        }

        delete raw;
        PostMessageW(notifyWindow, notifyMessage, 0, 0);
        return;
    }

    CloseHandle(thread); // detached - SetPercentAllThreadProc frees its own context
}

DWORD WINAPI MonitorControl::SetPercentAllThreadProc(LPVOID param)
{
    std::unique_ptr<SetPercentAllThreadContext> context(static_cast<SetPercentAllThreadContext*>(param));

    std::vector<PendingBrightness> results;
    results.reserve(context->targets.size());

    for (size_t i = 0; i < context->targets.size(); ++i)
    {
        const size_t index = context->targets[i].first;
        const HANDLE handle = context->targets[i].second;
        const DWORD rawValue = context->rawTargets[i];
        const bool ok = SetMonitorBrightness(handle, rawValue) != FALSE;
        results.push_back({ index, rawValue, ok });
    }

    context->self->pendingSetPercentAll_ = std::move(results);
    PostMessageW(context->notifyWindow, context->notifyMessage, 0, 0);
    return 0;
}

void MonitorControl::ApplyPendingSetPercentAll()
{
    for (const auto& result : pendingSetPercentAll_)
    {
        if (result.ok && result.index < monitors_.size())
        {
            monitors_[result.index].current = result.raw;
            Log(L"%s -> raw %lu (async, Set Value For All)",
                monitors_[result.index].description.c_str(), result.raw);
        }
        else if (!result.ok)
        {
            Log(L"SetMonitorBrightness failed (async, Set Value For All) for monitor index %zu.",
                result.index);
        }
    }

    pendingSetPercentAll_.clear();
    setPercentAllInProgress_ = false;

    if (hasQueuedPercentAll_)
    {
        hasQueuedPercentAll_ = false;
        SetPercentAllAsync(queuedPercentAll_, queuedNotifyWindow_, queuedNotifyMessage_);
    }
}

int MonitorControl::AdjustPercent(size_t index, int deltaPercent)
{
    if (index >= monitors_.size() || !monitors_[index].supported)
    {
        return -1;
    }

    const int target = Clamp(monitors_[index].Percent() + deltaPercent, 0, 100);
    return SetPercent(index, target) ? target : -1;
}

#if MONITORBRIGHTNESS_ENABLE_HOTKEYS
int MonitorControl::IndexUnderCursor() const
{
    POINT cursor{};
    if (!GetCursorPos(&cursor))
    {
        return -1;
    }

    const HMONITOR display = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
    if (display == nullptr)
    {
        return -1;
    }

    int fallback = -1;
    for (size_t i = 0; i < monitors_.size(); ++i)
    {
        if (monitors_[i].display != display)
        {
            continue;
        }

        if (monitors_[i].supported)
        {
            return static_cast<int>(i);
        }

        if (fallback < 0)
        {
            fallback = static_cast<int>(i);
        }
    }

    return fallback;
}
#endif // MONITORBRIGHTNESS_ENABLE_HOTKEYS
