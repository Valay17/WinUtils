#include "ZoomRenderer.h"

#include <magnification.h>

#include "Settings.h"

namespace
{

using MagInitializeFn = BOOL(WINAPI*)(void);
using MagUninitializeFn = BOOL(WINAPI*)(void);
using MagSetFullscreenTransformFn = BOOL(WINAPI*)(float, int, int);

HMODULE g_dll = nullptr;
MagInitializeFn g_magInitialize = nullptr;
MagUninitializeFn g_magUninitialize = nullptr;
MagSetFullscreenTransformFn g_magSetFullscreenTransform = nullptr;

bool LoadApi()
{
    if (g_dll != nullptr)
    {
        return g_magSetFullscreenTransform != nullptr;
    }

    g_dll = LoadLibraryExW(
        L"Magnification.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (g_dll == nullptr)
    {
        return false;
    }

    g_magInitialize = reinterpret_cast<MagInitializeFn>(
        reinterpret_cast<void*>(GetProcAddress(g_dll, "MagInitialize")));
    g_magUninitialize = reinterpret_cast<MagUninitializeFn>(
        reinterpret_cast<void*>(GetProcAddress(g_dll, "MagUninitialize")));
    g_magSetFullscreenTransform = reinterpret_cast<MagSetFullscreenTransformFn>(
        reinterpret_cast<void*>(GetProcAddress(g_dll, "MagSetFullscreenTransform")));

    return g_magInitialize != nullptr &&
           g_magUninitialize != nullptr &&
           g_magSetFullscreenTransform != nullptr;
}

void UnloadApi()
{
    if (g_dll != nullptr)
    {
        FreeLibrary(g_dll);
        g_dll = nullptr;
    }

    g_magInitialize = nullptr;
    g_magUninitialize = nullptr;
    g_magSetFullscreenTransform = nullptr;
}

constexpr wchar_t kOwnMarkerName[] = L"Local\\WinUtils_DesktopZoom_Magnifier";     // this utility's marker
constexpr wchar_t kOtherMarkerName[] = L"Local\\WinUtils_ColorInvertWindow_Magnifier"; // Color Invert Window's marker - name must match its own kOwnMarkerName exactly

int ClampInt(int value, int low, int high)
{
    return value < low ? low : (value > high ? high : value);
}

ZoomRenderer* g_renderer = nullptr;

} // namespace

ZoomRenderer::~ZoomRenderer()
{
    Shutdown();
}

bool ZoomRenderer::Initialize()
{
    if (!LoadApi())
    {
        Log(L"Magnification API unavailable - zoom disabled.");
        return false;
    }

    if (!g_magInitialize())
    {
        Log(L"MagInitialize failed (Win32 %lu) - zoom disabled.", GetLastError());
        UnloadApi();
        return false;
    }

    initialized_ = true;

    RefreshScreenMetrics();

    marker_ = CreateMutexW(nullptr, FALSE, kOwnMarkerName);

    const HANDLE other = OpenMutexW(SYNCHRONIZE, FALSE, kOtherMarkerName);
    otherMagnifierUserPresent_ = other != nullptr;
    if (other != nullptr)
    {
        CloseHandle(other);
    }

    level_ = 1.0f;
    POINT origin{ 0, 0 };
    Apply(1.0f, origin, /*quiet=*/false);

    return true;
}

void ZoomRenderer::Shutdown()
{
    StopFollowing();

    if (initialized_)
    {
        POINT origin{ 0, 0 };
        Apply(1.0f, origin, /*quiet=*/false);

        if (g_magUninitialize != nullptr)
        {
            g_magUninitialize();
        }

        initialized_ = false;
    }

    if (marker_ != nullptr)
    {
        CloseHandle(marker_);
        marker_ = nullptr;
    }

    otherMagnifierUserPresent_ = false;
    level_ = 1.0f;
}

float ZoomRenderer::Adjust(float delta, float maxZoom)
{
    if (!initialized_)
    {
        return level_;
    }

    float target = level_ + delta;
    if (target < 1.0f)
    {
        target = 1.0f;
    }
    if (target > maxZoom)
    {
        target = maxZoom;
    }

    if (target == level_)
    {
        return level_;
    }

    POINT pointer{};
    if (!GetCursorPos(&pointer))
    {
        pointer = { 0, 0 };
    }

    if (Apply(target, pointer, /*quiet=*/false))
    {
        level_ = target;
    }

    if (IsZoomed())
    {
        StartFollowing();
    }
    else
    {
        StopFollowing();
    }

    return level_;
}

void ZoomRenderer::Reset()
{
    if (!initialized_ || !IsZoomed())
    {
        return;
    }

    StopFollowing();

    POINT origin{ 0, 0 };
    if (Apply(1.0f, origin, /*quiet=*/false))
    {
        level_ = 1.0f;
        Log(L"zoom reset to 1.0x");
    }
}

void ZoomRenderer::RefreshScreenMetrics()
{
    screenLeft_ = GetSystemMetrics(SM_XVIRTUALSCREEN);
    screenTop_ = GetSystemMetrics(SM_YVIRTUALSCREEN);
    screenWidth_ = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    screenHeight_ = GetSystemMetrics(SM_CYVIRTUALSCREEN);

    Log(L"Screen metrics: %dx%d at (%d,%d)", screenWidth_, screenHeight_, screenLeft_, screenTop_);
}

void ZoomRenderer::ComputeOrigin(float level, POINT pointer, int& x, int& y) const  // origin = pointer * (1 - 1/level)
{
    x = 0;
    y = 0;

    if (level <= 1.001f)
    {
        return;
    }

    const int screenWidth = screenWidth_;
    const int screenHeight = screenHeight_;

    if (screenWidth <= 0 || screenHeight <= 0)
    {
        return;
    }

    const int viewWidth = static_cast<int>(screenWidth / level);
    const int viewHeight = static_cast<int>(screenHeight / level);

    const float factor = 1.0f - (1.0f / level);
    const int relativeX = pointer.x - screenLeft_;
    const int relativeY = pointer.y - screenTop_;

    x = ClampInt(static_cast<int>(relativeX * factor), 0, screenWidth - viewWidth) + screenLeft_;
    y = ClampInt(static_cast<int>(relativeY * factor), 0, screenHeight - viewHeight) + screenTop_;
}

bool ZoomRenderer::Apply(float level, POINT pointer, bool quiet)
{
    if (g_magSetFullscreenTransform == nullptr)
    {
        return false;
    }

    int offsetX = 0;
    int offsetY = 0;
    ComputeOrigin(level, pointer, offsetX, offsetY);

    if (!g_magSetFullscreenTransform(level, offsetX, offsetY))
    {
        Log(L"MagSetFullscreenTransform(%.2f, %d, %d) failed (Win32 %lu).",
            level, offsetX, offsetY, GetLastError());
        return false;
    }

    appliedX_ = offsetX;
    appliedY_ = offsetY;

    if (!quiet)
    {
        Log(L"zoom %.2fx, view origin (%d,%d)", level, offsetX, offsetY);
    }

    return true;
}

void ZoomRenderer::StartFollowing()
{
    if (pointerHook_ != nullptr)
    {
        return;
    }

    g_renderer = this;

    pointerHook_ = SetWinEventHook(
        EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
        nullptr, &ZoomRenderer::PointerHookProc,
        0, 0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

    if (pointerHook_ == nullptr)
    {
        Log(L"Pointer tracking unavailable (Win32 %lu) - the view will stay where "
            L"the last keypress put it.", GetLastError());
    }
}

void ZoomRenderer::StopFollowing()
{
    if (pointerHook_ != nullptr)
    {
        UnhookWinEvent(pointerHook_);
        pointerHook_ = nullptr;
    }

    g_renderer = nullptr;
}

void CALLBACK ZoomRenderer::PointerHookProc(
    HWINEVENTHOOK /*hook*/, DWORD event, HWND /*window*/,
    LONG objectId, LONG /*childId*/, DWORD /*threadId*/, DWORD /*timestamp*/)
{
    if (event != EVENT_OBJECT_LOCATIONCHANGE || objectId != OBJID_CURSOR)
    {
        return;
    }

    if (g_renderer != nullptr)
    {
        g_renderer->OnPointerMoved();
    }
}

void ZoomRenderer::OnPointerMoved()
{
    if (!initialized_ || !IsZoomed())
    {
        return;
    }

    POINT pointer{};
    if (!GetCursorPos(&pointer))
    {
        return;
    }

    int offsetX = 0;
    int offsetY = 0;
    ComputeOrigin(level_, pointer, offsetX, offsetY);

    if (offsetX == appliedX_ && offsetY == appliedY_)
    {
        return;
    }

    Apply(level_, pointer, /*quiet=*/true);
}
