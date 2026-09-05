#pragma once

#include <windows.h>

#include <string>

#ifndef MONITORBRIGHTNESS_ENABLE_HOTKEYS
#define MONITORBRIGHTNESS_ENABLE_HOTKEYS 0  // set to 1, or -DMONITORBRIGHTNESS_ENABLE_HOTKEYS=1, to enable
#endif

struct Hotkey
{
    UINT modifiers = 0;
    UINT vk = 0;
};

class Config
{
public:
    void Load();

    Hotkey brightnessUp{ MOD_CONTROL | MOD_ALT, VK_PRIOR };   // Ctrl+Alt+PageUp
    Hotkey brightnessDown{ MOD_CONTROL | MOD_ALT, VK_NEXT };  // Ctrl+Alt+PageDown

    UINT step = 10;  // percentage points per keypress

    static std::wstring Directory();
    static std::wstring FilePath();
};

const std::wstring& ExecutableDirectory();

void Log(const wchar_t* format, ...);
void LogAlways(const wchar_t* format, ...);
bool LoggingEnabled();

bool ReadIniInt(const std::wstring& path, const wchar_t* section, const wchar_t* key, UINT& value);
