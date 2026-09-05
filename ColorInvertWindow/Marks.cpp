#include "Marks.h"

#include "Config.h"
#include "FocusTracker.h"

#include <algorithm>

namespace
{

constexpr wchar_t kAlwaysSection[] = L"always";

constexpr wchar_t kLegacyMarkedSection[] = L"marked";
constexpr wchar_t kLegacyRememberedSection[] = L"remembered";

constexpr wchar_t kSeparator = L'|';

constexpr wchar_t kMetaSection[] = L"meta";
constexpr wchar_t kSeededKey[] = L"defaultsWritten";

void SeedAlwaysEntryIfAbsent(const std::wstring& path, const wchar_t* key, const wchar_t* value)
{
    wchar_t existing[8]{};
    GetPrivateProfileStringW(kAlwaysSection, key, L"", existing, static_cast<DWORD>(std::size(existing)), path.c_str());

    if (existing[0] == L'\0')
    {
        WritePrivateProfileStringW(kAlwaysSection, key, value, path.c_str());
    }
}

} // namespace

std::wstring Marks::FilePath()
{
    const std::wstring& directory = ExecutableDirectory();
    return directory.empty() ? std::wstring() : directory + L"marks.ini";
}

std::wstring Marks::Normalize(const std::wstring& executable)
{
    std::wstring lowered = executable;
    std::transform(lowered.begin(), lowered.end(), lowered.begin(),
                   [](wchar_t c) { return static_cast<wchar_t>(::towlower(c)); });
    return lowered;
}

bool Marks::Identify(HWND window, DWORD& processId, DWORD& threadId)
{
    processId = 0;
    threadId = 0;

    if (window == nullptr || !IsWindow(window))
    {
        return false;
    }

    threadId = GetWindowThreadProcessId(window, &processId);
    return threadId != 0 && processId != 0;
}

bool Marks::Contains(HWND window) const
{
    DWORD processId = 0;
    DWORD threadId = 0;
    if (!Identify(window, processId, threadId))
    {
        return false;
    }

    return std::any_of(entries_.begin(), entries_.end(), [&](const Entry& entry) {
        return entry.window == window &&
               entry.processId == processId &&
               entry.threadId == threadId;
    });
}

bool Marks::IsSuspended(HWND window) const
{
    DWORD processId = 0;
    DWORD threadId = 0;
    if (!Identify(window, processId, threadId))
    {
        return false;
    }

    return IsSuspended(window, processId, threadId);
}

bool Marks::IsSuspended(HWND window, DWORD processId, DWORD threadId) const
{
    return std::any_of(suspended_.begin(), suspended_.end(), [&](const Entry& entry) {
        return entry.window == window &&
               entry.processId == processId &&
               entry.threadId == threadId;
    });
}

bool Marks::Toggle(HWND window)
{
    DWORD processId = 0;
    DWORD threadId = 0;
    if (!Identify(window, processId, threadId))
    {
        return false;
    }

    const bool always = IsAlwaysWindow(window);

    const auto it = std::find_if(entries_.begin(), entries_.end(), [&](const Entry& entry) {
        return entry.window == window;
    });

    if (it != entries_.end())
    {
        const bool wasSameWindow = it->processId == processId && it->threadId == threadId;
        entries_.erase(it);

        if (wasSameWindow)
        {
            if (always)
            {
                if (!IsSuspended(window, processId, threadId))
                {
                    suspended_.push_back(Entry{ window, processId, threadId, always });
                }
            }

            return false;
        }
    }

    suspended_.erase(
        std::remove_if(suspended_.begin(), suspended_.end(), [&](const Entry& entry) {
            return entry.window == window &&
                   entry.processId == processId &&
                   entry.threadId == threadId;
        }),
        suspended_.end());

    entries_.push_back(Entry{ window, processId, threadId, always });
    return true;
}

void Marks::Clear()
{
    entries_.erase(
        std::remove_if(entries_.begin(), entries_.end(),
                       [](const Entry& entry) { return !entry.fromAlwaysRule; }),
        entries_.end());
}

size_t Marks::Prune()
{
    const size_t before = entries_.size();

    entries_.erase(
        std::remove_if(entries_.begin(), entries_.end(), [](const Entry& entry) {
            DWORD processId = 0;
            DWORD threadId = 0;
            if (!Identify(entry.window, processId, threadId))
            {
                return true;
            }

            return processId != entry.processId || threadId != entry.threadId;
        }),
        entries_.end());

    suspended_.erase(
        std::remove_if(suspended_.begin(), suspended_.end(), [](const Entry& entry) {
            DWORD processId = 0;
            DWORD threadId = 0;
            if (!Identify(entry.window, processId, threadId))
            {
                return true;
            }

            return processId != entry.processId || threadId != entry.threadId;
        }),
        suspended_.end());

    return before - entries_.size();
}

bool Marks::MatchesAnyRule(const std::wstring& executable, const std::wstring& title) const
{
    if (executable.empty())
    {
        return false;
    }

    for (const Rule& rule : rules_)
    {
        if (rule.executable != executable)
        {
            continue;
        }

        if (rule.title.empty() || rule.title == title)
        {
            return true;
        }
    }

    return false;
}

bool Marks::IsAlwaysWindow(const std::wstring& executable, const std::wstring& title) const
{
    return !rules_.empty() && MatchesAnyRule(Normalize(executable), title);
}

bool Marks::IsAlwaysWindow(HWND window) const
{
    if (rules_.empty() || window == nullptr)
    {
        return false;
    }

    return IsAlwaysWindow(
        FocusTracker::ExecutableForWindow(window),
        FocusTracker::TitleForWindow(window, 0));
}

bool Marks::ApplyRulesTo(HWND window, const std::wstring& executable, const std::wstring& title)
{
    if (rules_.empty() || window == nullptr || Contains(window) || IsSuspended(window))
    {
        return false;
    }

    DWORD processId = 0;
    DWORD threadId = 0;
    if (!Identify(window, processId, threadId))
    {
        return false;
    }

    if (!IsAlwaysWindow(executable, title))
    {
        return false;
    }

    entries_.push_back(Entry{ window, processId, threadId, true });
    return true;
}

bool Marks::ApplyRulesTo(HWND window)
{
    if (rules_.empty() || window == nullptr || Contains(window) || IsSuspended(window))
    {
        return false;
    }

    return ApplyRulesTo(window,
                        FocusTracker::ExecutableForWindow(window),
                        FocusTracker::TitleForWindow(window, 0));
}

BOOL CALLBACK Marks::EnumProc(HWND window, LPARAM parameter)
{
    auto* const self = reinterpret_cast<Marks*>(parameter);

    if (IsWindowVisible(window) && GetWindowTextLengthW(window) > 0)
    {
        self->ApplyRulesTo(window);
    }

    return TRUE;
}

size_t Marks::ApplyRulesToAllWindows()
{
    if (rules_.empty())
    {
        return 0;
    }

    const size_t before = entries_.size();
    EnumWindows(&Marks::EnumProc, reinterpret_cast<LPARAM>(this));
    return entries_.size() - before;
}

void Marks::LoadRules()
{
    rules_.clear();

    const std::wstring path = FilePath();
    if (path.empty())
    {
        return;
    }

    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        WriteTemplateFile(path);
    }

    WritePrivateProfileStringW(kLegacyMarkedSection, nullptr, nullptr, path.c_str());
    WritePrivateProfileStringW(kLegacyRememberedSection, nullptr, nullptr, path.c_str());

    if (GetPrivateProfileIntW(kMetaSection, kSeededKey, 0, path.c_str()) == 0)
    {
        SeedAlwaysEntryIfAbsent(path, L"1", L"taskmgr.exe");
        SeedAlwaysEntryIfAbsent(path, L"2", L"openhardwaremonitor.exe");
        WritePrivateProfileStringW(kMetaSection, kSeededKey, L"1", path.c_str());
        FlushIniCache(path);
    }

    std::vector<wchar_t> buffer(16384);
    const DWORD read = GetPrivateProfileSectionW(
        kAlwaysSection, buffer.data(), static_cast<DWORD>(buffer.size()), path.c_str());

    if (read == 0 || read >= buffer.size() - 2)
    {
        return;
    }

    const wchar_t* entry = buffer.data();
    while (*entry != L'\0')
    {
        const std::wstring line(entry);
        entry += line.size() + 1;

        const size_t equals = line.find(L'=');
        std::wstring value = (equals == std::wstring::npos) ? line : line.substr(equals + 1);

        const size_t first = value.find_first_not_of(L" \t");
        if (first == std::wstring::npos)
        {
            continue;
        }

        const size_t last = value.find_last_not_of(L" \t");
        value = value.substr(first, last - first + 1);

        const size_t separator = value.find(kSeparator);

        Rule rule;
        rule.executable = Normalize(
            separator == std::wstring::npos ? value : value.substr(0, separator));
        rule.title = separator == std::wstring::npos ? std::wstring() : value.substr(separator + 1);

        if (!rule.executable.empty())
        {
            rules_.push_back(std::move(rule));
        }
    }
}

void Marks::WriteTemplateFile(const std::wstring& path) const
{
    static const wchar_t kTemplate[] =
        L"; Color Invert Window\r\n"
        L";\r\n"
        L"; Applications listed here are ALWAYS inverted while focused.\r\n"
        L"; They are applied at startup and to any matching window opened later, and \"clear all marks\" does not remove them.\r\n"
        L";\r\n"
        L"; The hotkey does NOT write to this file.\r\n"
        L"; A window marked by hotkey is inverted for this session only, and only that exact window - which is what keeps two Notepad windows independent.\r\n"
        L";\r\n"
        L"; Pressing the hotkey on a window listed here suspends it until you press the hotkey again, or until the app restarts.\r\n"
        L";\r\n"
        L"; Format, one per line:\r\n"
        L";   <executable>              every window of that program\r\n"
        L";   <executable>|<title>      only windows with exactly that title,\r\n"
        L";                             for programs that share a host process -\r\n"
        L";                             Calculator and Sticky Notes are both\r\n"
        L";                             applicationframehost.exe, for example.\r\n"
        L"\r\n"
        L"; To add another, focus its window with this app running and read the executable name out of the 'focus -> hwnd ... <name>.exe' line in ColorInvertWindow.log.\r\n"
        L"\r\n"
        L"[always]\r\n"
        L"1=taskmgr.exe\r\n"
        L"2=openhardwaremonitor.exe\r\n";

    const HANDLE file = CreateFileW(
        path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);

    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    const unsigned char bom[] = { 0xEF, 0xBB, 0xBF };
    DWORD written = 0;
    WriteFile(file, bom, sizeof(bom), &written, nullptr);

    const int bytes = WideCharToMultiByte(CP_UTF8, 0, kTemplate, -1, nullptr, 0, nullptr, nullptr);
    if (bytes > 1)
    {
        std::vector<char> utf8(static_cast<size_t>(bytes));
        WideCharToMultiByte(CP_UTF8, 0, kTemplate, -1, utf8.data(), bytes, nullptr, nullptr);
        WriteFile(file, utf8.data(), static_cast<DWORD>(bytes - 1), &written, nullptr);
    }

    CloseHandle(file);
}
