#include <windows.h>
#include <shellapi.h>

#include <cstdarg>
#include <cstdio>
#include <string>

#include "Config.h"
#include "Resources.h"
#include "Win32Raii.h"
#include "FocusTracker.h"
#include "Inverter.h"
#include "Marks.h"

namespace
{

constexpr wchar_t kWindowClass[] = L"ColorInvertWindow_MessageWindow";
constexpr wchar_t kSingleInstanceMutex[] = L"Local\\ColorInvertWindow_SingleInstance";

// Signaled by another process to ask for a clean exit.
constexpr wchar_t kShutdownEvent[] = L"Local\\ColorInvertWindow_Shutdown";

// Broadcast when inversion toggles, so Screen Dimmer can compensate.
constexpr wchar_t kInversionMessageName[] = L"ColorInvertWindow_InversionChanged";

constexpr UINT WM_TRAY_ICON = WM_APP + 1;
constexpr UINT kHotkeyId = 1;
constexpr UINT kTrayIconId = 1;

constexpr UINT kMenuClearAll = 101;
constexpr UINT kMenuExit = 104;
constexpr UINT kMenuStartup = 105;

Config g_config;
Marks g_marks;
Inverter g_inverter;
FocusTracker g_focusTracker;

HWND g_window = nullptr;
UINT g_taskbarCreatedMessage = 0;
UINT g_inversionMessage = 0;
bool g_hotkeyRegistered = false;

void BroadcastInversionState(bool inverted)
{
    if (g_inversionMessage != 0)
    {
        PostMessageW(HWND_BROADCAST, g_inversionMessage, inverted ? 1 : 0, 0);
    }
}

const std::wstring& LogPath()
{
    static const std::wstring path = []() -> std::wstring {
        const std::wstring& directory = ExecutableDirectory();
        return directory.empty() ? std::wstring() : directory + L"ColorInvertWindow.log";
    }();

    return path;
}

bool LoggingEnabled()
{
    static const bool enabled = []() -> bool {
        const std::wstring& directory = ExecutableDirectory();
        if (directory.empty())
        {
            return false;
        }

        return GetFileAttributesW((directory + L"logging.on").c_str())
               != INVALID_FILE_ATTRIBUTES;
    }();

    return enabled;
}

void Log(const wchar_t* format, ...)
{
    if (!LoggingEnabled())
    {
        return;
    }

    const std::wstring& path = LogPath();
    if (path.empty())
    {
        return;
    }

    const HANDLE file = CreateFileW(
        path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);

    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    SYSTEMTIME now{};
    GetLocalTime(&now);

    wchar_t message[1024];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, ARRAYSIZE(message), _TRUNCATE, format, args);
    va_end(args);

    wchar_t line[1200];
    _snwprintf_s(line, _TRUNCATE, L"%04d-%02d-%02d %02d:%02d:%02d.%03d  %s\r\n",
               now.wYear, now.wMonth, now.wDay,
               now.wHour, now.wMinute, now.wSecond, now.wMilliseconds,
               message);

    const int bytes = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
    if (bytes > 1)
    {
        std::string utf8(static_cast<size_t>(bytes - 1), '\0');
        WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8.data(), bytes, nullptr, nullptr);

        DWORD written = 0;
        WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }

    CloseHandle(file);
}


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

void BuildTooltip(wchar_t* buffer, size_t capacity)
{
    if (!g_inverter.HasMagnifier())
    {
        wcscpy_s(buffer, capacity, L"Color Invert Window - unavailable (magnifier in use)");
        return;
    }

    _snwprintf_s(buffer, capacity, _TRUNCATE, L"Color Invert Window - %zu window(s) marked%s",
               g_marks.SessionCount(),
               g_inverter.IsInverted() ? L", inverted now" : L"");
}

void UpdateTrayIcon(bool add)
{
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = g_window;
    data.uID = kTrayIconId;
    data.uFlags = add ? (NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP)
                      : (NIF_TIP | NIF_SHOWTIP);

    data.uCallbackMessage = WM_TRAY_ICON;
    data.hIcon = g_trayIcon;
    BuildTooltip(data.szTip, ARRAYSIZE(data.szTip));

    if (Shell_NotifyIconW(add ? NIM_ADD : NIM_MODIFY, &data))
    {
        if (add)
        {
            NOTIFYICONDATAW version{};
            version.cbSize = sizeof(version);
            version.hWnd = g_window;
            version.uID = kTrayIconId;
            version.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, &version);
        }
    }
    else
    {
        Log(L"Shell_NotifyIcon(%s) failed (Win32 %lu).%s",
            add ? L"NIM_ADD" : L"NIM_MODIFY", GetLastError(),
            add ? L" Waiting for TaskbarCreated to retry." : L"");
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
    wcscpy_s(data.szInfoTitle, ARRAYSIZE(data.szInfoTitle), title);
    wcscpy_s(data.szInfo, ARRAYSIZE(data.szInfo), text);
    Shell_NotifyIconW(NIM_MODIFY, &data);
}

constexpr wchar_t kRunKeyPath[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kRunValueName[] = L"ColorInvertWindow";

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

void ApplyForWindow(HWND window,
                    const std::wstring* executable = nullptr,
                    const std::wstring* title = nullptr)
{
    const size_t dropped = g_marks.Prune();

    const bool marked = (executable != nullptr && title != nullptr)
        ? g_marks.ApplyRulesTo(window, *executable, *title)
        : g_marks.ApplyRulesTo(window);

    if (marked)
    {
        Log(L"hwnd 0x%p matches a remembered rule - marked", static_cast<void*>(window));
    }

    const bool shouldInvert = g_marks.Contains(window);
    const bool before = g_inverter.IsInverted();

    g_inverter.SetInverted(shouldInvert);

    const bool after = g_inverter.IsInverted();
    const bool changed = after != before;

    if (changed)
    {
        BroadcastInversionState(after);
    }

    if (changed || dropped > 0)
    {
        UpdateTrayIcon(false);
    }

    if (dropped > 0)
    {
        Log(L"%zu marked window(s) closed - mark dropped, %zu remaining",
            dropped, g_marks.Count());
    }
}

void ApplyForCurrentFocus()
{
    ApplyForWindow(GetForegroundWindow());
}

bool IsTransientShellWindow(const std::wstring& windowClass)
{
    static const wchar_t* kTransientClasses[] = {
        L"MultitaskingViewFrame",         // Windows 10 Alt+Tab and Task View
        L"XamlExplorerHostIslandWindow",  // Windows 11 Alt+Tab
        L"TaskSwitcherWnd",               // older Alt+Tab
        L"TaskSwitcherOverlayWnd",        // older Alt+Tab overlay
        L"ForegroundStaging",             // transient window used during switches
    };

    for (const wchar_t* candidate : kTransientClasses)
    {
        if (windowClass == candidate)
        {
            return true;
        }
    }

    return false;
}

void OnFocusChanged(HWND window, const std::wstring& executable, const std::wstring& windowClass)
{
    if (IsTransientShellWindow(windowClass))
    {
        Log(L"focus -> [%s] ignored (transient shell window), holding inversion %s",
            windowClass.c_str(), g_inverter.IsInverted() ? L"on" : L"off");
        return;
    }

    const std::wstring title = FocusTracker::TitleForWindow(window, 0);

    std::wstring shortTitle = title;
    if (shortTitle.size() > 40)
    {
        shortTitle.resize(37);
        shortTitle += L"...";
    }

    Log(L"focus -> hwnd 0x%p %s [%s] \"%s\"",
        static_cast<void*>(window),
        executable.empty() ? L"(unknown)" : executable.c_str(),
        windowClass.c_str(),
        shortTitle.c_str());

    ApplyForWindow(window, &executable, &title);
}

std::wstring DescribeWindow(HWND window)
{
    const std::wstring title = FocusTracker::TitleForWindow(window);
    if (!title.empty())
    {
        return L"\"" + title + L"\"";
    }

    const std::wstring executable = FocusTracker::ExecutableForWindow(window);
    return executable.empty() ? std::wstring(L"this window") : (L"a " + executable + L" window");
}

void ToggleWindow(HWND window)
{
    if (window == nullptr || window == g_window || !IsWindow(window))
    {
        Log(L"hotkey pressed with no markable window focused - nothing to do");
        ShowBalloon(L"Color Invert Window", L"There is no focused window to mark.");
        return;
    }

    const std::wstring description = DescribeWindow(window);

    const bool always = g_marks.IsAlwaysWindow(window);
    const bool nowMarked = g_marks.Toggle(window);

    const wchar_t* state = nowMarked
        ? (always ? L"inverted again" : L"now inverted when focused")
        : (always ? L"suspended until you press the hotkey again or restart"
                  : L"no longer inverted");

    Log(L"%s hwnd 0x%p %s - %s%s",
        nowMarked ? L"marked" : (always ? L"suspended" : L"unmarked"),
        static_cast<void*>(window), description.c_str(), state,
        always ? L" [always rule]" : L"");

    ApplyForWindow(window);
    UpdateTrayIcon(false);
}

void EnableDarkMenus()
{
    const HMODULE module = LoadLibraryExW(L"uxtheme.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (module == nullptr)
    {
        return;
    }

    using SetPreferredAppModeFn = int(WINAPI*)(int);
    using FlushMenuThemesFn = void(WINAPI*)();

    const auto setPreferredAppMode = reinterpret_cast<SetPreferredAppModeFn>(
        reinterpret_cast<void*>(GetProcAddress(module, MAKEINTRESOURCEA(135))));  // SetPreferredAppMode
    const auto flushMenuThemes = reinterpret_cast<FlushMenuThemesFn>(
        reinterpret_cast<void*>(GetProcAddress(module, MAKEINTRESOURCEA(136))));  // FlushMenuThemes

    if (setPreferredAppMode == nullptr || flushMenuThemes == nullptr)
    {
        return;
    }

    constexpr int kAllowDark = 1; // 0 Default, 1 AllowDark, 2 ForceDark, 3 ForceLight, 4 Max.
    setPreferredAppMode(kAllowDark);
    flushMenuThemes();
}

void ApplyDarkMenuBackground(HMENU menu)
{
    static const HBRUSH darkBrush = CreateSolidBrush(RGB(0x2b, 0x2b, 0x2b));

    MENUINFO info{};
    info.cbSize = sizeof(info);
    info.fMask = MIM_BACKGROUND;
    info.hbrBack = darkBrush;
    SetMenuInfo(menu, &info);
}

void ShowContextMenu()
{
    const HMENU menu = CreatePopupMenu();
    if (menu == nullptr)
    {
        return;
    }
    ApplyDarkMenuBackground(menu);

    const UINT clearFlags = g_marks.SessionCount() == 0 ? (MF_STRING | MF_GRAYED) : MF_STRING;
    wchar_t clearLabel[64];
    _snwprintf_s(clearLabel, _TRUNCATE, L"&Clear Inversion (%zu)", g_marks.SessionCount());
    AppendMenuW(menu, clearFlags, kMenuClearAll, clearLabel);

    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);

    AppendMenuW(menu, IsStartupEnabled() ? (MF_STRING | MF_CHECKED) : MF_STRING,
                kMenuStartup, L"Start With &Windows");

    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, kMenuExit, L"E&xit");

    POINT anchor{};
    RECT iconRect{};

    if (TrayIconRect(iconRect))
    {
        anchor.x = iconRect.left;
        anchor.y = iconRect.top;
    }
    else
    {
        GetCursorPos(&anchor);
    }

    SetForegroundWindow(g_window);
    TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN,
                   anchor.x, anchor.y, 0, g_window, nullptr);
    PostMessageW(g_window, WM_NULL, 0, 0);

    DestroyMenu(menu);
}

DWORD WINAPI ShutdownWatcher(LPVOID parameter)
{
    const HANDLE event = static_cast<HANDLE>(parameter);

    if (WaitForSingleObject(event, INFINITE) == WAIT_OBJECT_0)
    {
        PostMessageW(g_window, WM_CLOSE, 0, 0);
    }

    return 0;
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
        if (wParam == kHotkeyId)
        {
            ToggleWindow(GetForegroundWindow());
        }
        return 0;

    case WM_TRAY_ICON:
        switch (LOWORD(lParam))
        {
        case WM_CONTEXTMENU:
        case WM_RBUTTONUP:
        case WM_LBUTTONUP:
        case NIN_SELECT:
        case NIN_KEYSELECT:
            ShowContextMenu();
            break;
        default:
            break;
        }
        return 0;

    case WM_COMMAND:
        switch (LOWORD(wParam))
        {
        case kMenuClearAll:
            g_marks.Clear();
            ApplyForCurrentFocus();
            UpdateTrayIcon(false);
            Log(L"session marks cleared from the menu - [always] rules in marks.ini "
                L"are unaffected");
            break;
        case kMenuStartup:
            SetStartupEnabled(!IsStartupEnabled());
            break;
        case kMenuExit:
            Log(L"exit chosen from the tray menu");
            PostQuitMessage(0);
            break;
        default:
            break;
        }
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    default:
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    const HANDLE rawLock = CreateMutexW(nullptr, TRUE, kSingleInstanceMutex);
    const bool alreadyRunning = GetLastError() == ERROR_ALREADY_EXISTS;

    const raii::OwnedMutex instanceLock(rawLock, rawLock != nullptr && !alreadyRunning);
    if (!instanceLock.Valid() || alreadyRunning)
    {
        return 0;
    }

    g_config.Load();
    g_marks.LoadRules();
    EnableDarkMenus();

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = kWindowClass;

    if (RegisterClassExW(&windowClass) == 0)
    {
        return 1;
    }

    g_window = CreateWindowExW(
        WS_EX_TOOLWINDOW, kWindowClass, L"ColorInvertWindow", WS_POPUP, 0, 0, 0, 0,
        nullptr, nullptr, instance, nullptr);

    if (g_window == nullptr)
    {
        return 1;
    }

    g_taskbarCreatedMessage = RegisterWindowMessageW(L"TaskbarCreated");
    g_inversionMessage = RegisterWindowMessageW(kInversionMessageName);

    const raii::UniqueHandle shutdownEvent(CreateEventW(nullptr, TRUE, FALSE, kShutdownEvent));
    raii::UniqueHandle shutdownThread;
    if (shutdownEvent.Valid())
    {
        shutdownThread.Reset(
            CreateThread(nullptr, 0, &ShutdownWatcher, shutdownEvent.Get(), 0, nullptr));
    }

    const bool magnifierReady = g_inverter.Initialize();

    LoadTrayIcon(instance);
    UpdateTrayIcon(true);

    if (!magnifierReady)
    {
        ShowBalloon(
            L"Color Invert Window",
            L"The Magnification API is unavailable on this system. Marks can still be "
            L"edited, but inversion is disabled. See logs.");
    }

    g_hotkeyRegistered = RegisterHotKey(
        g_window, kHotkeyId, g_config.hotkey.modifiers | MOD_NOREPEAT, g_config.hotkey.vk) != FALSE;

    Log(L"=== Color Invert Window start === hotkey modifiers=%u vk=%u registered=%s",
        g_config.hotkey.modifiers, g_config.hotkey.vk, g_hotkeyRegistered ? L"yes" : L"no");

    const size_t restored = g_marks.ApplyRulesToAllWindows();

    Log(L"Magnifier available: %s, [always] rules: %zu, windows marked at startup: %zu",
        g_inverter.HasMagnifier() ? L"yes" : L"no", g_marks.RuleCount(), restored);

    for (const auto& rule : g_marks.Rules())
    {
        Log(L"  always: %s%s%s", rule.executable.c_str(),
            rule.title.empty() ? L" (any window)" : L" - title must be ",
            rule.title.c_str());
    }
    Log(L"Desktop Zoom also running: %s%s",
        g_inverter.OtherMagnifierUserPresent() ? L"yes" : L"no",
        g_inverter.OtherMagnifierUserPresent()
            ? L" - both drive the fullscreen magnifier; watch whether zoom and inversion compose or interfere"
            : L"");

    if (!g_hotkeyRegistered)
    {
        Log(L"WARNING: the hotkey did not register. Something else owns it.");
    }

    g_focusTracker.Start(&OnFocusChanged);

    ApplyForCurrentFocus();

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    g_focusTracker.Stop();

    if (g_hotkeyRegistered)
    {
        UnregisterHotKey(g_window, kHotkeyId);
    }

    g_inverter.Shutdown();
    BroadcastInversionState(false);

    RemoveTrayIcon();
    DestroyWindow(g_window);

    if (shutdownEvent.Valid())
    {
        SetEvent(shutdownEvent.Get());

        if (shutdownThread.Valid())
        {
            WaitForSingleObject(shutdownThread.Get(), 1000);
        }
    }

    Log(L"=== Color Invert Window end ===");

    return 0;
}
