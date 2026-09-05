#include <windows.h>
#include <shellapi.h>

#include <algorithm>
#include <string>

#include "Resources.h"
#include "Settings.h"
#include "ZoomRenderer.h"

namespace
{

constexpr wchar_t kWindowClass[] = L"DesktopZoom_MessageWindow";
constexpr wchar_t kSingleInstanceMutex[] = L"Local\\DesktopZoom_SingleInstance";
constexpr wchar_t kShutdownEvent[] = L"Local\\DesktopZoom_Shutdown";

constexpr UINT WM_TRAY_ICON = WM_APP + 1;
constexpr UINT kTrayIconId = 1;

constexpr int kHotkeyZoomIn = 1;
constexpr int kHotkeyZoomOut = 2;
constexpr int kHotkeyReset = 3;

constexpr UINT kMenuZoomIn = 100;
constexpr UINT kMenuZoomOut = 101;
constexpr UINT kMenuReset = 102;
constexpr UINT kMenuExit = 105;
constexpr UINT kMenuStartup = 106;

ZoomRenderer g_zoom;

HWND g_window = nullptr;
UINT g_taskbarCreatedMessage = 0;

struct HotkeyRegistration
{
    int id;
    UINT modifiers;
    UINT vk;
    const wchar_t* description;
    bool registered;
};

HotkeyRegistration g_hotkeys[3];

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
    data.uFlags = add ? (NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP)
                      : (NIF_TIP | NIF_SHOWTIP);

    data.uCallbackMessage = WM_TRAY_ICON;
    data.hIcon = g_trayIcon;

    if (!g_zoom.Available())
    {
        wcscpy_s(data.szTip, ARRAYSIZE(data.szTip), L"Desktop Zoom - unavailable");
    }
    else
    {
        _snwprintf_s(data.szTip, _TRUNCATE, L"Desktop Zoom: %.2fx", g_zoom.Level());
    }

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

void RegisterHotkeys()
{
    g_hotkeys[0] = { kHotkeyZoomIn, settings::kHotkeyModifiers, settings::kZoomInVk,
                     L"zoom in", false };
    g_hotkeys[1] = { kHotkeyZoomOut, settings::kHotkeyModifiers, settings::kZoomOutVk,
                     L"zoom out", false };
    g_hotkeys[2] = { kHotkeyReset, settings::kHotkeyModifiers, settings::kResetVk,
                     L"reset zoom", false };

    int failures = 0;
    for (auto& hotkey : g_hotkeys)
    {
        hotkey.registered =
            RegisterHotKey(g_window, hotkey.id, hotkey.modifiers, hotkey.vk) != FALSE;

        Log(L"hotkey %s: %s", hotkey.description,
            hotkey.registered ? L"registered" : L"TAKEN by another application");

        if (!hotkey.registered)
        {
            ++failures;
        }
    }

    if (failures > 0)
    {
        Log(L"WARNING: %d hotkey(s) did not register. Something else owns them.", failures);
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


constexpr wchar_t kRunKeyPath[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kRunValueName[] = L"DesktopZoom";

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

void ApplyZoom(float delta)
{
    if (!g_zoom.Available())
    {
        return;
    }

    const float before = g_zoom.Level();
    if (g_zoom.Adjust(delta, settings::kMaxZoom) != before)
    {
        UpdateTrayIcon(false);
    }
}

void ResetZoom()
{
    g_zoom.Reset();
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

    wchar_t level[64];
    _snwprintf_s(level, _TRUNCATE, L"Desktop Zoom: %.2fx", g_zoom.Level());
    AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, level);
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);

    AppendMenuW(menu, MF_STRING, kMenuZoomIn, L"Zoom &In");
    AppendMenuW(menu, g_zoom.IsZoomed() ? MF_STRING : (MF_STRING | MF_GRAYED),
                kMenuZoomOut, L"Zoom &Out");
    AppendMenuW(menu, g_zoom.IsZoomed() ? MF_STRING : (MF_STRING | MF_GRAYED),
                kMenuReset, L"&Reset to 100%");

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
        switch (static_cast<int>(wParam))
        {
        case kHotkeyZoomIn:  ApplyZoom(settings::kZoomStep); break;
        case kHotkeyZoomOut: ApplyZoom(-settings::kZoomStep); break;
        case kHotkeyReset:   ResetZoom(); break;
        default: break;
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
        case kMenuZoomIn:  ApplyZoom(settings::kZoomStep); break;
        case kMenuZoomOut: ApplyZoom(-settings::kZoomStep); break;
        case kMenuReset:   ResetZoom(); break;

        case kMenuStartup:
            SetStartupEnabled(!IsStartupEnabled());
            break;

        case kMenuExit:
            PostQuitMessage(0);
            break;

        default:
            break;
        }
        return 0;

    case WM_DISPLAYCHANGE:
        g_zoom.RefreshScreenMetrics();
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
        PostMessageW(g_window, WM_CLOSE, 0, 0);
    }

    return 0;
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    const HANDLE instanceLock = CreateMutexW(nullptr, TRUE, kSingleInstanceMutex);
    if (instanceLock == nullptr || GetLastError() == ERROR_ALREADY_EXISTS)
    {
        if (instanceLock != nullptr)
        {
            CloseHandle(instanceLock);
        }
        return 0;
    }

    RemoveStrayConfigFile();
    EnableDarkMenus();

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

    g_window = CreateWindowExW(
        WS_EX_TOOLWINDOW, kWindowClass, L"DesktopZoom", WS_POPUP, 0, 0, 0, 0,
        nullptr, nullptr, instance, nullptr);

    if (g_window == nullptr)
    {
        CloseHandle(instanceLock);
        return 1;
    }

    g_taskbarCreatedMessage = RegisterWindowMessageW(L"TaskbarCreated");

    const HANDLE shutdownEvent = CreateEventW(nullptr, TRUE, FALSE, kShutdownEvent);
    HANDLE shutdownThread = nullptr;
    if (shutdownEvent != nullptr)
    {
        shutdownThread = CreateThread(nullptr, 0, &ShutdownWatcher, shutdownEvent, 0, nullptr);
    }

    Log(L"=== Desktop Zoom start === (keyboard-driven magnification, hotkeys only)");

    const bool zoomReady = g_zoom.Initialize();

    Log(L"Zoom available: %s, step %.2f, max %.2fx, pointer tracking: proportional (KDE)",
        zoomReady ? L"yes" : L"no", settings::kZoomStep, settings::kMaxZoom);
    Log(L"Color Invert Window also running: %s%s",
        g_zoom.OtherMagnifierUserPresent() ? L"yes" : L"no",
        g_zoom.OtherMagnifierUserPresent()
            ? L" - a color effect and a transform compose without interfering"
            : L"");

    LoadTrayIcon(instance);
    UpdateTrayIcon(true);
    RegisterHotkeys();

    if (!zoomReady)
    {
        ShowBalloon(L"Desktop Zoom",
                    L"The Magnification API is unavailable on this system, so zoom is "
                    L"disabled. See logs.");
    }

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    UnregisterHotkeys();

    g_zoom.Shutdown();

    RemoveTrayIcon();
    DestroyWindow(g_window);

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

    Log(L"=== Desktop Zoom end ===");

    ReleaseMutex(instanceLock);
    CloseHandle(instanceLock);
    return 0;
}
