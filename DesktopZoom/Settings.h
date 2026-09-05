#pragma once

#include <windows.h>

#include <string>

namespace settings
{

constexpr float kZoomStep = 0.25f;
constexpr float kMaxZoom = 8.0f;

constexpr UINT kHotkeyModifiers = MOD_CONTROL | MOD_ALT;  // Ctrl+Alt
constexpr UINT kZoomInVk = VK_OEM_PLUS;                   // =
constexpr UINT kZoomOutVk = VK_OEM_MINUS;                 // -
constexpr UINT kResetVk = '0';                            // 0

} // namespace settings

const std::wstring& ExecutableDirectory();
void RemoveStrayConfigFile();
bool LoggingEnabled();
void Log(const wchar_t* format, ...);
