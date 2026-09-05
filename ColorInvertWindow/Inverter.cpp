#include "Inverter.h"

#include <magnification.h>

namespace
{

using MagInitializeFn = BOOL(WINAPI*)(void);
using MagUninitializeFn = BOOL(WINAPI*)(void);
using MagSetFullscreenColorEffectFn = BOOL(WINAPI*)(PMAGCOLOREFFECT);

HMODULE g_magnificationDll = nullptr;
MagInitializeFn g_magInitialize = nullptr;
MagUninitializeFn g_magUninitialize = nullptr;
MagSetFullscreenColorEffectFn g_magSetFullscreenColorEffect = nullptr;

bool LoadMagnificationApi()
{
    if (g_magnificationDll != nullptr)
    {
        return g_magSetFullscreenColorEffect != nullptr;
    }

    g_magnificationDll = LoadLibraryExW(
        L"Magnification.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (g_magnificationDll == nullptr)
    {
        return false;
    }

    g_magInitialize =
        reinterpret_cast<MagInitializeFn>(
            reinterpret_cast<void*>(GetProcAddress(g_magnificationDll, "MagInitialize")));
    g_magUninitialize =
        reinterpret_cast<MagUninitializeFn>(
            reinterpret_cast<void*>(GetProcAddress(g_magnificationDll, "MagUninitialize")));
    g_magSetFullscreenColorEffect =
        reinterpret_cast<MagSetFullscreenColorEffectFn>(
            reinterpret_cast<void*>(GetProcAddress(g_magnificationDll, "MagSetFullscreenColorEffect")));

    return g_magInitialize != nullptr &&
           g_magUninitialize != nullptr &&
           g_magSetFullscreenColorEffect != nullptr;
}

void UnloadMagnificationApi()
{
    if (g_magnificationDll != nullptr)
    {
        FreeLibrary(g_magnificationDll);
        g_magnificationDll = nullptr;
    }

    g_magInitialize = nullptr;
    g_magUninitialize = nullptr;
    g_magSetFullscreenColorEffect = nullptr;
}

// invert = 1 - channel
const MAGCOLOREFFECT kInvert = { {
    { -1.0f,  0.0f,  0.0f, 0.0f, 0.0f },
    {  0.0f, -1.0f,  0.0f, 0.0f, 0.0f },
    {  0.0f,  0.0f, -1.0f, 0.0f, 0.0f },
    {  0.0f,  0.0f,  0.0f, 1.0f, 0.0f },
    {  1.0f,  1.0f,  1.0f, 0.0f, 1.0f },
} };

const MAGCOLOREFFECT kIdentity = { {
    { 1.0f, 0.0f, 0.0f, 0.0f, 0.0f },
    { 0.0f, 1.0f, 0.0f, 0.0f, 0.0f },
    { 0.0f, 0.0f, 1.0f, 0.0f, 0.0f },
    { 0.0f, 0.0f, 0.0f, 1.0f, 0.0f },
    { 0.0f, 0.0f, 0.0f, 0.0f, 1.0f },
} };

constexpr wchar_t kOwnMarkerName[] = L"Local\\WinUtils_ColorInvertWindow_Magnifier"; // this utility's marker
constexpr wchar_t kOtherMarkerName[] = L"Local\\WinUtils_DesktopZoom_Magnifier";     // Desktop Zoom's marker - name must match its own kOwnMarkerName exactly

} // namespace

Inverter::~Inverter()
{
    Shutdown();
}

bool Inverter::Initialize()
{
    if (!LoadMagnificationApi())
    {
        return false;
    }

    if (!g_magInitialize())
    {
        UnloadMagnificationApi();
        return false;
    }
    initialized_ = true;

    magnifierMutex_ = CreateMutexW(nullptr, FALSE, kOwnMarkerName);

    const HANDLE other = OpenMutexW(SYNCHRONIZE, FALSE, kOtherMarkerName);
    otherMagnifierUserPresent_ = other != nullptr;
    if (other != nullptr)
    {
        CloseHandle(other);
    }

    ownsMagnifier_ = true;

    Apply(false);
    return true;
}

void Inverter::Shutdown()
{
    if (initialized_)
    {
        if (ownsMagnifier_)
        {
            Apply(false);
        }

        if (g_magUninitialize != nullptr)
        {
            g_magUninitialize();
        }
        initialized_ = false;
    }

    if (magnifierMutex_ != nullptr)
    {
        CloseHandle(magnifierMutex_);
        magnifierMutex_ = nullptr;
    }

    UnloadMagnificationApi();

    ownsMagnifier_ = false;
    otherMagnifierUserPresent_ = false;
    inverted_ = false;
}

void Inverter::SetInverted(bool inverted)
{
    if (!initialized_ || !ownsMagnifier_ || inverted == inverted_)
    {
        return;
    }

    if (Apply(inverted))
    {
        inverted_ = inverted;
    }
}

bool Inverter::Apply(bool inverted)
{
    if (g_magSetFullscreenColorEffect == nullptr)
    {
        return false;
    }

    MAGCOLOREFFECT effect = inverted ? kInvert : kIdentity;
    return g_magSetFullscreenColorEffect(&effect) != FALSE;
}
