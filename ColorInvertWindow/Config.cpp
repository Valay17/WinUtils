#include "Config.h"

#include <string>

namespace
{

constexpr wchar_t kHotkeySection[] = L"hotkey";
constexpr int kMissing = -1;

} // namespace

const std::wstring& ExecutableDirectory()
{
    static const std::wstring directory = []() -> std::wstring {
        wchar_t executable[MAX_PATH];
        const DWORD length = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));

        if (length == 0 || length >= ARRAYSIZE(executable))
        {
            return {};
        }

        const std::wstring full(executable, length);
        const size_t separator = full.find_last_of(L'\\');
        return separator == std::wstring::npos ? std::wstring() : full.substr(0, separator + 1);
    }();

    return directory;
}

std::wstring Config::Directory()
{
    const std::wstring& directory = ExecutableDirectory();
    if (directory.empty())
    {
        return {};
    }

    return directory.substr(0, directory.size() - 1);
}

std::wstring Config::FilePath()
{
    const std::wstring directory = Directory();
    return directory.empty() ? std::wstring() : directory + L"\\config.ini";
}

bool ReadIniInt(const std::wstring& path, const wchar_t* section, const wchar_t* key, UINT& value)
{
    if (path.empty())
    {
        return false;
    }

    const int read = static_cast<int>(
        GetPrivateProfileIntW(section, key, kMissing, path.c_str()));

    if (read == kMissing || read < 0)
    {
        return false;
    }

    value = static_cast<UINT>(read);
    return true;
}

void FlushIniCache(const std::wstring& path)
{
    if (!path.empty())
    {
        WritePrivateProfileStringW(nullptr, nullptr, nullptr, path.c_str());
    }
}

void Config::Load()
{
    const std::wstring path = FilePath();
    if (path.empty())
    {
        return;
    }

    UINT value = 0;

    if (ReadIniInt(path, kHotkeySection, L"modifiers", value) && value > 0)
    {
        hotkey.modifiers = value;
    }

    if (ReadIniInt(path, kHotkeySection, L"vk", value) && value > 0)
    {
        hotkey.vk = value;
    }
}
