#include <windows.h>
#include <dbghelp.h>
#include <dbt.h>
#include <shellapi.h>

#include <algorithm>
#include <string>

#include "Config.h"
#include "CustomValuePopup.h"
#include "MonitorControl.h"
#include "Resources.h"
#include "SliderPopup.h"

namespace
{

constexpr wchar_t kWindowClass[] = L"MonitorBrightness_MessageWindow";
constexpr wchar_t kSingleInstanceMutex[] = L"Local\\MonitorBrightness_SingleInstance";

// Signaled by another process to ask for a clean exit.
constexpr wchar_t kShutdownEvent[] = L"Local\\MonitorBrightness_Shutdown";

constexpr UINT WM_TRAY_ICON = WM_APP + 1;
constexpr UINT WM_MONITORS_REFRESHED = WM_APP + 2;
constexpr UINT WM_SET_PERCENT_ALL_DONE = WM_APP + 3;
constexpr UINT WM_CUSTOM_TRIGGER_READY = WM_APP + 4;

constexpr UINT kTrayIconId = 1;

constexpr int kHotkeyUp = 1;
constexpr int kHotkeyDown = 2;
constexpr int kHotkeyUpAll = 3;
constexpr int kHotkeyDownAll = 4;

constexpr UINT kMenuExit = 100;
constexpr UINT kMenuRefresh = 101;
constexpr UINT kMenuStartup = 102;
constexpr UINT kMenuAllBase = 200;      // + preset index
constexpr UINT kMenuMonitorBase = 1000; // + monitor * 100 + preset index

const int kPresets[] = { 0, 25, 50, 75, 100 };
constexpr int kPresetCount = static_cast<int>(sizeof(kPresets) / sizeof(kPresets[0]));

Config g_config;
MonitorControl g_monitors;
SliderPopup g_sliderPopup;
CustomValuePopup g_customValuePopup;

HWND g_window = nullptr;
UINT g_taskbarCreatedMessage = 0;
HDEVNOTIFY g_deviceNotify = nullptr;

// Only valid while ShowContextMenu's own TrackPopupMenu call is tracking.
HMENU g_customTriggerMenu = nullptr;

constexpr wchar_t kCustomTriggerPlaceholder[] = L"Adjust Value using Slider";

// GUID_DEVINTERFACE_MONITOR
const GUID kMonitorInterface =
    { 0xe6f07b5f, 0xee97, 0x4a90, { 0xb0, 0x76, 0x33, 0xf5, 0x7b, 0xf4, 0xea, 0xa7 } };

LONG WINAPI WriteCrashDump(EXCEPTION_POINTERS* exceptionPointers)
{
    LogAlways(L"UNHANDLED EXCEPTION: code 0x%08lX at address %p - writing a crash dump.",
        exceptionPointers->ExceptionRecord->ExceptionCode,
        exceptionPointers->ExceptionRecord->ExceptionAddress);

    const std::wstring& directory = ExecutableDirectory();
    if (!directory.empty())
    {
        const std::wstring dumpPath = directory + L"MonitorBrightness.dmp";
        const HANDLE file = CreateFileW(
            dumpPath.c_str(), GENERIC_WRITE, 0, nullptr,
            CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);

        if (file != INVALID_HANDLE_VALUE)
        {
            MINIDUMP_EXCEPTION_INFORMATION info{};
            info.ThreadId = GetCurrentThreadId();
            info.ExceptionPointers = exceptionPointers;
            info.ClientPointers = FALSE;

            MiniDumpWriteDump(
                GetCurrentProcess(), GetCurrentProcessId(), file,
                static_cast<MINIDUMP_TYPE>(MiniDumpNormal | MiniDumpWithThreadInfo),
                &info, nullptr, nullptr);

            CloseHandle(file);
        }
    }

    return EXCEPTION_EXECUTE_HANDLER;
}

void EnableDarkMode()
{
    // Not constexpr: MAKEINTRESOURCEA is an integer-to-pointer cast, which is
    // not a core constant expression in standard C++.
    const LPCSTR kSetPreferredAppModeOrdinal = MAKEINTRESOURCEA(135);
    const LPCSTR kFlushMenuThemesOrdinal = MAKEINTRESOURCEA(136);
    constexpr int kAllowDark = 1;

    const HMODULE uxtheme = LoadLibraryExW(L"uxtheme.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (uxtheme == nullptr)
    {
        return;
    }

    using SetPreferredAppModeFn = int(WINAPI*)(int);
    using FlushMenuThemesFn = void(WINAPI*)(void);

    const auto setMode = reinterpret_cast<SetPreferredAppModeFn>(
        reinterpret_cast<void*>(GetProcAddress(uxtheme, kSetPreferredAppModeOrdinal)));
    const auto flush = reinterpret_cast<FlushMenuThemesFn>(
        reinterpret_cast<void*>(GetProcAddress(uxtheme, kFlushMenuThemesOrdinal)));

    if (setMode != nullptr)
    {
        setMode(kAllowDark);
    }

    if (flush != nullptr)
    {
        flush();
    }
}

// Handle owned by the module; shared, valid for the life of the process, and
// must not be passed to DestroyIcon.
HICON g_trayIcon = nullptr;

void LoadTrayIcon(HINSTANCE instance)
{
    g_trayIcon = LoadIconW(instance, MAKEINTRESOURCEW(IDI_TRAY));

    if (g_trayIcon == nullptr)
    {
        Log(L"WARNING: the tray icon resource could not be loaded (Win32 %lu). The "
            L"tray icon will be blank.", GetLastError());
    }
}

void UpdateTrayIcon(bool add)
{
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = g_window;
    data.uID = kTrayIconId;
    // NIF_ICON/NIF_MESSAGE only on NIM_ADD - neither ever changes afterward,
    // and re-declaring them on every NIM_MODIFY makes the shell re-evaluate
    // the icon's tray placement on every brightness change.
    //
    // NIF_SHOWTIP is required on both paths: NOTIFYICON_VERSION_4 (negotiated
    // below) turns the standard tooltip off by default without it.
    data.uFlags = add ? (NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP)
                      : (NIF_TIP | NIF_SHOWTIP);
    data.uCallbackMessage = WM_TRAY_ICON;
    data.hIcon = g_trayIcon;

    std::wstring tip;
    for (const auto& monitor : g_monitors.Monitors())
    {
        if (!monitor.supported)
        {
            continue;
        }

        if (!tip.empty())
        {
            tip += L", ";
        }

        wchar_t entry[48];
        _snwprintf_s(entry, _TRUNCATE, L"%s - %d%%", monitor.label.c_str(), monitor.Percent());
        tip += entry;
    }

    if (tip.empty())
    {
        tip = L"Brightness - no DDC/CI monitors found";
    }

    wcsncpy_s(data.szTip, ARRAYSIZE(data.szTip), tip.c_str(), _TRUNCATE);

    if (Shell_NotifyIconW(add ? NIM_ADD : NIM_MODIFY, &data) && add)
    {
        // NOTIFYICON_VERSION_4: delivers NIN_SELECT/NIN_KEYSELECT for keyboard
        // activation instead of the shell synthesizing a mouse click at the icon.
        NOTIFYICONDATAW version{};
        version.cbSize = sizeof(version);
        version.hWnd = g_window;
        version.uID = kTrayIconId;
        version.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &version);
    }
}

bool TrayIconRect(RECT& rect)
{
    NOTIFYICONIDENTIFIER identifier{};
    identifier.cbSize = sizeof(identifier);
    identifier.hWnd = g_window;
    identifier.uID = kTrayIconId;

    return Shell_NotifyIconGetRect(&identifier, &rect) == S_OK;
}

POINT TrayAnchorPoint()
{
    RECT iconRect{};
    POINT anchor{};

    if (TrayIconRect(iconRect))
    {
        anchor.x = iconRect.left;
        anchor.y = iconRect.top;
    }
    else
    {
        GetCursorPos(&anchor);
    }

    return anchor;
}

void RemoveTrayIcon()
{
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = g_window;
    data.uID = kTrayIconId;
    Shell_NotifyIconW(NIM_DELETE, &data);
}

void ShowBalloon(const wchar_t* title, const wchar_t* text)
{
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = g_window;
    data.uID = kTrayIconId;
    data.uFlags = NIF_INFO;
    data.dwInfoFlags = NIIF_INFO;
    wcsncpy_s(data.szInfoTitle, ARRAYSIZE(data.szInfoTitle), title, _TRUNCATE);
    wcsncpy_s(data.szInfo, ARRAYSIZE(data.szInfo), text, _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &data);
}

#if MONITORBRIGHTNESS_ENABLE_HOTKEYS

struct HotkeyRegistration
{
    int id;
    UINT modifiers;
    UINT vk;
    const wchar_t* description;
    bool registered;
};

HotkeyRegistration g_hotkeys[4];

void RegisterHotkeys()
{
    g_hotkeys[0] = { kHotkeyUp, g_config.brightnessUp.modifiers,
                     g_config.brightnessUp.vk, L"brightness up", false };
    g_hotkeys[1] = { kHotkeyDown, g_config.brightnessDown.modifiers,
                     g_config.brightnessDown.vk, L"brightness down", false };

    // Shift added to the same key means "all monitors".
    g_hotkeys[2] = { kHotkeyUpAll, g_config.brightnessUp.modifiers | MOD_SHIFT,
                     g_config.brightnessUp.vk, L"brightness up (all monitors)", false };
    g_hotkeys[3] = { kHotkeyDownAll, g_config.brightnessDown.modifiers | MOD_SHIFT,
                     g_config.brightnessDown.vk, L"brightness down (all monitors)", false };

    int failures = 0;
    for (auto& hotkey : g_hotkeys)
    {
        hotkey.registered = RegisterHotKey(
            g_window, hotkey.id, hotkey.modifiers | MOD_NOREPEAT, hotkey.vk) != FALSE;

        Log(L"hotkey %s: %s", hotkey.description,
            hotkey.registered ? L"registered" : L"TAKEN by another application");

        if (!hotkey.registered)
        {
            ++failures;
        }
    }

    if (failures > 0)
    {
        ShowBalloon(L"Monitor Brightness",
                    L"Some hotkeys are already taken by other applications. See logs.");
    }
}

void UnregisterHotkeys()
{
    for (const auto& hotkey : g_hotkeys)
    {
        if (hotkey.registered)
        {
            UnregisterHotKey(g_window, hotkey.id);
        }
    }
}

#endif // MONITORBRIGHTNESS_ENABLE_HOTKEYS


constexpr wchar_t kRunKeyPath[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kRunValueName[] = L"MonitorBrightness";

std::wstring ExecutablePath()
{
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
    return (length == 0 || length >= ARRAYSIZE(path)) ? std::wstring() : std::wstring(path, length);
}

bool IsStartupEnabled()
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKeyPath, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS)
    {
        return false;
    }

    const LSTATUS status = RegQueryValueExW(key, kRunValueName, nullptr, nullptr, nullptr, nullptr);
    RegCloseKey(key);
    return status == ERROR_SUCCESS;
}

void SetStartupEnabled(bool enabled)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKeyPath, 0, KEY_SET_VALUE, &key) != ERROR_SUCCESS)
    {
        Log(L"Could not open the Run key (Win32 %lu).", GetLastError());
        return;
    }

    if (enabled)
    {
        const std::wstring path = ExecutablePath();
        if (path.empty())
        {
            Log(L"Could not determine this executable's path; startup not enabled.");
            RegCloseKey(key);
            return;
        }

        // Quoted, because a path containing spaces would otherwise be split at
        // the first one when Windows reads this value back.
        const std::wstring quoted = L"\"" + path + L"\"";
        const LSTATUS status = RegSetValueExW(
            key, kRunValueName, 0, REG_SZ,
            reinterpret_cast<const BYTE*>(quoted.c_str()),
            static_cast<DWORD>((quoted.size() + 1) * sizeof(wchar_t)));

        Log(status == ERROR_SUCCESS
            ? L"Enabled start with Windows."
            : L"Could not write the Run value.");
    }
    else
    {
        RegDeleteValueW(key, kRunValueName);
        Log(L"Disabled start with Windows.");
    }

    RegCloseKey(key);
}

#if MONITORBRIGHTNESS_ENABLE_HOTKEYS

void AdjustUnderCursor(int delta)
{
    const int index = g_monitors.IndexUnderCursor();
    if (index < 0)
    {
        Log(L"hotkey ignored - no monitor under the cursor.");
        return;
    }

    const auto& monitor = g_monitors.Monitors()[static_cast<size_t>(index)];
    if (!monitor.supported)
    {
        Log(L"hotkey ignored - %s does not support DDC/CI.", monitor.description.c_str());
        ShowBalloon(L"Monitor Brightness",
                    L"That display cannot be controlled over DDC/CI. If it is the laptop "
                    L"screen this is expected; Windows controls that one itself.");
        return;
    }

    if (g_monitors.AdjustPercent(static_cast<size_t>(index), delta) >= 0)
    {
        UpdateTrayIcon(false);
    }
}

void AdjustAll(int delta)
{
    bool changed = false;
    for (size_t i = 0; i < g_monitors.Monitors().size(); ++i)
    {
        if (g_monitors.Monitors()[i].supported &&
            g_monitors.AdjustPercent(i, delta) >= 0)
        {
            changed = true;
        }
    }

    if (changed)
    {
        UpdateTrayIcon(false);
    }
}

#endif // MONITORBRIGHTNESS_ENABLE_HOTKEYS

// Guards against a single tray-icon right-click firing both WM_RBUTTONUP and
// WM_CONTEXTMENU, which would otherwise run ShowContextMenu reentrantly.
bool g_showingMenu = false;

void ShowContextMenu()
{
    if (g_showingMenu)
    {
        return;
    }
    g_showingMenu = true;

    const HMENU menu = CreatePopupMenu();
    if (menu == nullptr)
    {
        g_showingMenu = false;
        return;
    }

    const auto& monitors = g_monitors.Monitors();

    if (monitors.empty())
    {
        AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L"No monitors detected");
    }
    else if (g_monitors.SupportedCount() == 0)
    {
        AppendMenuW(menu, MF_STRING | MF_GRAYED, 0,
                    L"No monitor supports DDC/CI");
    }

    auto presetLabel = [](int percent, wchar_t* out, size_t outCount) {
        _snwprintf_s(out, outCount, _TRUNCATE, L"&%d%%", percent);
    };

    if (g_monitors.SupportedCount() > 0)
    {
        const HMENU all = CreatePopupMenu();

        g_customTriggerMenu = CreatePopupMenu();
        AppendMenuW(g_customTriggerMenu, MF_STRING | MF_GRAYED, 0, kCustomTriggerPlaceholder);
        AppendMenuW(all, MF_POPUP, reinterpret_cast<UINT_PTR>(g_customTriggerMenu), L"&Custom…");

        for (int p = 0; p < kPresetCount; ++p)
        {
            wchar_t label[16];
            presetLabel(kPresets[p], label, ARRAYSIZE(label));
            AppendMenuW(all, MF_STRING, kMenuAllBase + static_cast<UINT>(p), label);
        }

        AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(all), L"Set &Value For All");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    }
    else
    {
        g_customTriggerMenu = nullptr;
    }

    for (size_t i = 0; i < monitors.size(); ++i)
    {
        const MonitorInfo& monitor = monitors[i];

        if (!monitor.supported)
        {
            std::wstring label = monitor.label + L" (no DDC/CI)";
            AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, label.c_str());
            continue;
        }

        const HMENU submenu = CreatePopupMenu();
        for (int p = 0; p < kPresetCount; ++p)
        {
            wchar_t label[16];
            presetLabel(kPresets[p], label, ARRAYSIZE(label));

            const UINT id = kMenuMonitorBase +
                static_cast<UINT>(monitor.displayNumber) * 100 + static_cast<UINT>(p);

            const UINT flags = (monitor.Percent() == kPresets[p])
                ? (MF_STRING | MF_CHECKED)
                : MF_STRING;

            AppendMenuW(submenu, flags, id, label);
        }

        wchar_t label[160];
        _snwprintf_s(label, _TRUNCATE, L"Monitor &%d  -  %d%%",
                     monitor.displayNumber, monitor.Percent());
        AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(submenu), label);
    }

    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, kMenuRefresh, L"&Re-detect Monitors");

    // Read fresh every time the menu opens - anything on the machine can
    // change this value, so a state captured once would drift.
    AppendMenuW(menu, IsStartupEnabled() ? (MF_STRING | MF_CHECKED) : MF_STRING,
                kMenuStartup, L"Start With &Windows");

    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, kMenuExit, L"E&xit");

    const POINT cursor = TrayAnchorPoint();

    // Required so the menu dismisses correctly from a message-only window.
    SetForegroundWindow(g_window);

    TrackPopupMenu(menu, TPM_RIGHTBUTTON, cursor.x, cursor.y, 0, g_window, nullptr);
    PostMessageW(g_window, WM_NULL, 0, 0);

    DestroyMenu(menu);

    // Reset here so a WM_CUSTOM_TRIGGER_READY that arrives after the menu is
    // already dismissed can't reference this now-destroyed HMENU.
    g_customTriggerMenu = nullptr;
    g_showingMenu = false;
}

// Decoded by display number rather than vector index - TrackPopupMenu runs
// its own nested message loop, which can still trigger a refresh that
// reorders monitors_ before a menu click is processed.
int FindMonitorIndexByDisplayNumber(int displayNumber)
{
    const auto& monitors = g_monitors.Monitors();
    for (size_t i = 0; i < monitors.size(); ++i)
    {
        if (monitors[i].displayNumber == displayNumber)
        {
            return static_cast<int>(i);
        }
    }
    return -1;
}

void HandleMenuCommand(UINT id)
{
    if (id >= kMenuMonitorBase)
    {
        const UINT offset = id - kMenuMonitorBase;
        const int displayNumber = static_cast<int>(offset / 100);
        const int preset = static_cast<int>(offset % 100);

        const int monitor = FindMonitorIndexByDisplayNumber(displayNumber);
        if (monitor < 0)
        {
            Log(L"HandleMenuCommand: Monitor %d is no longer present - ignoring the menu click.",
                displayNumber);
            return;
        }

        if (preset < kPresetCount && g_monitors.SetPercent(static_cast<size_t>(monitor), kPresets[preset]))
        {
            UpdateTrayIcon(false);
        }
        return;
    }

    if (id >= kMenuAllBase && id < kMenuAllBase + kPresetCount)
    {
        const int preset = static_cast<int>(id - kMenuAllBase);
        g_monitors.SetPercentAllAsync(kPresets[preset], g_window, WM_SET_PERCENT_ALL_DONE);
        return;
    }

    switch (id)
    {
    case kMenuRefresh:
        // Hides the slider popup first - its rows also bake in a vector index
        // that a refresh could invalidate mid-drag.
        g_sliderPopup.Hide();
        g_monitors.RefreshAsync(g_window, WM_MONITORS_REFRESHED);
        break;

    case kMenuStartup:
        SetStartupEnabled(!IsStartupEnabled());
        break;

    case kMenuExit:
        PostQuitMessage(0);
        break;

    default:
        break;
    }
}

bool IsCustomTriggerMenu(HMENU openingMenu, const wchar_t* verb, const wchar_t* matchAction)
{
    const bool match = g_customTriggerMenu != nullptr && openingMenu == g_customTriggerMenu;
    Log(L"%s: openingMenu=0x%p, g_customTriggerMenu=0x%p - %s.",
        verb, openingMenu, g_customTriggerMenu, match ? matchAction : L"no match, ignoring");
    return match;
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == g_taskbarCreatedMessage && g_taskbarCreatedMessage != 0)
    {
        UpdateTrayIcon(true);
        return 0;
    }

    switch (message)
    {
    case WM_HOTKEY:
#if MONITORBRIGHTNESS_ENABLE_HOTKEYS
        switch (static_cast<int>(wParam))
        {
        case kHotkeyUp:      AdjustUnderCursor(static_cast<int>(g_config.step)); break;
        case kHotkeyDown:    AdjustUnderCursor(-static_cast<int>(g_config.step)); break;
        case kHotkeyUpAll:   AdjustAll(static_cast<int>(g_config.step)); break;
        case kHotkeyDownAll: AdjustAll(-static_cast<int>(g_config.step)); break;
        default: break;
        }
#endif
        return 0;

    case WM_TRAY_ICON:
        // Left-click opens the live slider popup; right-click opens the preset
        // menu. NIN_SELECT/NIN_KEYSELECT are the keyboard equivalents of a left
        // click, WM_CONTEXTMENU of a right click - all three require
        // NOTIFYICON_VERSION_4 (see UpdateTrayIcon).
        switch (LOWORD(lParam))
        {
        case WM_LBUTTONUP:
        case NIN_SELECT:
        case NIN_KEYSELECT:
            Log(L"WM_TRAY_ICON: 0x%04X (%s) - opening slider popup.", LOWORD(lParam),
                LOWORD(lParam) == WM_LBUTTONUP ? L"mouse left-click" : L"keyboard select");
            g_sliderPopup.Show(g_monitors, TrayAnchorPoint());
            break;

        case WM_RBUTTONUP:
            Log(L"WM_TRAY_ICON: WM_RBUTTONUP (mouse right-click) - opening context menu.");
            ShowContextMenu();
            break;

        case WM_CONTEXTMENU:
            Log(L"WM_TRAY_ICON: WM_CONTEXTMENU (context-menu key) - opening context menu.");
            ShowContextMenu();
            break;

        default:
            break;
        }
        return 0;

    case WM_COMMAND:
        HandleMenuCommand(LOWORD(wParam));
        return 0;

    case WM_INITMENUPOPUP:
    {
        // Fires before Windows lays the child submenu out, so its real screen
        // rect isn't resolvable yet - deferred to WM_CUSTOM_TRIGGER_READY,
        // posted here and pumped on the next iteration of this same
        // TrackPopupMenu loop, by which point the submenu is actually on screen.
        const HMENU openingMenu = reinterpret_cast<HMENU>(wParam);
        if (!IsCustomTriggerMenu(openingMenu, L"WM_INITMENUPOPUP", L"match, posting WM_CUSTOM_TRIGGER_READY"))
        {
            return 0;
        }

        PostMessageW(window, WM_CUSTOM_TRIGGER_READY, reinterpret_cast<WPARAM>(openingMenu), 0);
        return 0;
    }

    case WM_CUSTOM_TRIGGER_READY:
    {
        const HMENU openingMenu = reinterpret_cast<HMENU>(wParam);
        if (!IsCustomTriggerMenu(openingMenu, L"WM_CUSTOM_TRIGGER_READY", L"match, resolving anchor"))
        {
            return 0;
        }

        RECT anchor{};
        const BOOL gotRect = GetMenuItemRect(window, openingMenu, 0, &anchor);
        Log(L"WM_CUSTOM_TRIGGER_READY: GetMenuItemRect %s - rect=(%ld,%ld,%ld,%ld).",
            gotRect ? L"succeeded" : L"FAILED", anchor.left, anchor.top, anchor.right, anchor.bottom);
        if (!gotRect)
        {
            return 0;
        }

        g_customValuePopup.Show(g_monitors, anchor);
        return 0;
    }

    case WM_DEVICECHANGE:
        // A monitor arriving or leaving invalidates every physical handle, so
        // the whole set is rebuilt rather than patched.
        if (wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE)
        {
            Log(L"display configuration changed - re-enumerating.");
            g_sliderPopup.Hide();
            g_customValuePopup.Hide(L"WM_DEVICECHANGE - monitors re-enumerating");
            g_monitors.RefreshAsync(g_window, WM_MONITORS_REFRESHED);
        }
        return TRUE;

    case WM_DISPLAYCHANGE:
        Log(L"WM_DISPLAYCHANGE - re-enumerating.");
        g_sliderPopup.Hide();
        g_customValuePopup.Hide(L"WM_DISPLAYCHANGE - monitors re-enumerating");
        g_monitors.RefreshAsync(g_window, WM_MONITORS_REFRESHED);
        return 0;

    case WM_MONITORS_REFRESHED:
        g_monitors.ApplyPendingRefresh();
        UpdateTrayIcon(false);
        return 0;

    case WM_SET_PERCENT_ALL_DONE:
        g_monitors.ApplyPendingSetPercentAll();
        UpdateTrayIcon(false);
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    default:
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

DWORD WINAPI ShutdownWatcher(LPVOID parameter)
{
    const HANDLE event = static_cast<HANDLE>(parameter);

    if (WaitForSingleObject(event, INFINITE) == WAIT_OBJECT_0)
    {
        // Posted, not sent: this is not the UI thread, and the message loop
        // must be what acts on it so shutdown runs in the usual order.
        PostMessageW(g_window, WM_CLOSE, 0, 0);
    }

    return 0;
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    SetUnhandledExceptionFilter(WriteCrashDump);
    EnableDarkMode();

    const HANDLE instanceLock = CreateMutexW(nullptr, TRUE, kSingleInstanceMutex);
    if (instanceLock == nullptr || GetLastError() == ERROR_ALREADY_EXISTS)
    {
        if (instanceLock != nullptr)
        {
            CloseHandle(instanceLock);
        }
        return 0;
    }

    g_config.Load();

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = kWindowClass;

    if (RegisterClassExW(&windowClass) == 0)
    {
        CloseHandle(instanceLock);
        return 1;
    }

    // A normal hidden window, not HWND_MESSAGE: message-only windows do not
    // receive WM_DEVICECHANGE broadcasts, which this utility needs to notice a
    // monitor being plugged in.
    g_window = CreateWindowExW(
        0, kWindowClass, L"Monitor Brightness", 0, 0, 0, 0, 0,
        nullptr, nullptr, instance, nullptr);

    if (g_window == nullptr)
    {
        CloseHandle(instanceLock);
        return 1;
    }

    g_taskbarCreatedMessage = RegisterWindowMessageW(L"TaskbarCreated");

    // Manual-reset so a signal cannot be missed by a race with the wait starting.
    const HANDLE shutdownEvent = CreateEventW(nullptr, TRUE, FALSE, kShutdownEvent);
    HANDLE shutdownThread = nullptr;
    if (shutdownEvent != nullptr)
    {
        shutdownThread = CreateThread(nullptr, 0, &ShutdownWatcher, shutdownEvent, 0, nullptr);
    }

    Log(L"=== Monitor Brightness start ===");

    DEV_BROADCAST_DEVICEINTERFACE_W filter{};
    filter.dbcc_size = sizeof(filter);
    filter.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE;
    filter.dbcc_classguid = kMonitorInterface;
    g_deviceNotify = RegisterDeviceNotificationW(
        g_window, &filter, DEVICE_NOTIFY_WINDOW_HANDLE);

    if (g_deviceNotify == nullptr)
    {
        Log(L"WARNING: RegisterDeviceNotification failed (Win32 %lu). Monitors plugged "
            L"in later will need 'Re-detect Monitors' from the tray menu.",
            GetLastError());
    }

    const int supported = g_monitors.Refresh();

    LoadTrayIcon(instance);
    UpdateTrayIcon(true);
#if MONITORBRIGHTNESS_ENABLE_HOTKEYS
    RegisterHotkeys();
#endif

    if (!g_sliderPopup.Create(instance))
    {
        Log(L"WARNING: the slider popup window could not be created (Win32 %lu). "
            L"Left-clicking the tray icon will do nothing; the right-click menu "
            L"still works.", GetLastError());
    }

    g_sliderPopup.OnCommitted = []() { UpdateTrayIcon(false); };

    if (!g_customValuePopup.Create(instance))
    {
        Log(L"WARNING: the Custom-value popup window could not be created (Win32 %lu). "
            L"The right-click menu's other 'Set Value For All' entries still work.",
            GetLastError());
    }

    g_customValuePopup.OnCommit = [](int percent)
    {
        g_monitors.SetPercentAllAsync(percent, g_window, WM_SET_PERCENT_ALL_DONE);
    };

    if (supported == 0)
    {
        ShowBalloon(
            L"Monitor Brightness",
            L"No monitor responded to DDC/CI. Check that it is enabled in the "
            L"monitor's own OSD menu - it is often off by default. See logs.");
    }

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

#if MONITORBRIGHTNESS_ENABLE_HOTKEYS
    UnregisterHotkeys();
#endif

    if (g_deviceNotify != nullptr)
    {
        UnregisterDeviceNotification(g_deviceNotify);
    }

    g_monitors.Release();
    RemoveTrayIcon();
    g_sliderPopup.Destroy();
    g_customValuePopup.Destroy();
    DestroyWindow(g_window);

    // The watcher is blocked on the event; signalling it lets the thread return
    // rather than being torn down mid-wait at process exit.
    if (shutdownEvent != nullptr)
    {
        SetEvent(shutdownEvent);

        if (shutdownThread != nullptr)
        {
            WaitForSingleObject(shutdownThread, 1000);
            CloseHandle(shutdownThread);
        }

        CloseHandle(shutdownEvent);
    }

    Log(L"=== Monitor Brightness end ===");

    ReleaseMutex(instanceLock);
    CloseHandle(instanceLock);
    return 0;
}
