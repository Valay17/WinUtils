#include "Config.h"


#include <cstdarg>
#include <cstdio>

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

void Config::Load()
{
    const std::wstring path = FilePath();
    if (path.empty())
    {
        return;
    }

    UINT value = 0;

    if (ReadIniInt(path, kHotkeySection, L"upModifiers", value) && value > 0)
    {
        brightnessUp.modifiers = value;
    }
    if (ReadIniInt(path, kHotkeySection, L"upVk", value) && value > 0)
    {
        brightnessUp.vk = value;
    }

    if (ReadIniInt(path, kHotkeySection, L"downModifiers", value) && value > 0)
    {
        brightnessDown.modifiers = value;
    }
    if (ReadIniInt(path, kHotkeySection, L"downVk", value) && value > 0)
    {
        brightnessDown.vk = value;
    }

    if (ReadIniInt(path, L"behavior", L"step", value))
    {
        step = (value < 1) ? 1 : ((value > 50) ? 50 : value);
    }
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

namespace
{

void LogImpl(bool force, const wchar_t* format, va_list args)
{
    if (!force && !LoggingEnabled())
    {
        return;
    }

    const std::wstring& directory = ExecutableDirectory();
    if (directory.empty())
    {
        return;
    }

    const std::wstring path = directory + L"MonitorBrightness.log";

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
    _vsnwprintf_s(message, ARRAYSIZE(message), _TRUNCATE, format, args);

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

} // namespace

void Log(const wchar_t* format, ...)
{
    va_list args;
    va_start(args, format);
    LogImpl(false, format, args);
    va_end(args);
}

void LogAlways(const wchar_t* format, ...)
{
    va_list args;
    va_start(args, format);
    LogImpl(true, format, args);
    va_end(args);
}
