#pragma once

#include <windows.h>

#include <string>

struct Hotkey
{
    UINT modifiers = MOD_WIN;  // Win
    UINT vk = 'C';             // C
};

class Config
{
public:
    void Load();

    Hotkey hotkey;

    static std::wstring Directory();
    static std::wstring FilePath();
};

const std::wstring& ExecutableDirectory();

bool ReadIniInt(const std::wstring& path, const wchar_t* section, const wchar_t* key, UINT& value);
void FlushIniCache(const std::wstring& path);
