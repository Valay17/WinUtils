#include "Settings.h"

#include <cstdarg>
#include <cstdio>

#include <string>

namespace
{
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

void RemoveStrayConfigFile()
{
    const std::wstring& directory = ExecutableDirectory();
    if (directory.empty())
    {
        return;
    }

    const std::wstring path = directory + L"config.ini";

    const HANDLE file = CreateFileW(
        path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);

    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    char firstByte = '\0';
    DWORD bytesRead = 0;
    const bool read = ReadFile(file, &firstByte, 1, &bytesRead, nullptr);
    CloseHandle(file);

    if (read && bytesRead == 1 && firstByte == '{')
    {
        DeleteFileW(path.c_str());
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

void Log(const wchar_t* format, ...)
{
    if (!LoggingEnabled())
    {
        return;
    }

    const std::wstring& directory = ExecutableDirectory();
    if (directory.empty())
    {
        return;
    }

    const std::wstring path = directory + L"DesktopZoom.log";

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
