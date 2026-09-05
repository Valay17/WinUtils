#pragma once

#include <windows.h>

#include <algorithm>
#include <string>
#include <vector>

class Marks
{
public:
    struct Entry
    {
        HWND window = nullptr;
        DWORD processId = 0;
        DWORD threadId = 0;

        bool fromAlwaysRule = false;
    };

    struct Rule
    {
        std::wstring executable;  // lowercased
        std::wstring title;       // empty means "any window of this executable"
    };

    void LoadRules();

    bool Contains(HWND window) const;

    bool Toggle(HWND window);

    void Clear();

    size_t Prune();

    bool ApplyRulesTo(HWND window);

    bool ApplyRulesTo(HWND window, const std::wstring& executable, const std::wstring& title);

    size_t ApplyRulesToAllWindows();

    size_t Count() const { return entries_.size(); }

    size_t SessionCount() const
    {
        return static_cast<size_t>(std::count_if(
            entries_.begin(), entries_.end(),
            [](const Entry& entry) { return !entry.fromAlwaysRule; }));
    }
    size_t RuleCount() const { return rules_.size(); }
    size_t SuspendedCount() const { return suspended_.size(); }
    bool Empty() const { return entries_.empty(); }

    const std::vector<Entry>& All() const { return entries_; }
    const std::vector<Rule>& Rules() const { return rules_; }

    bool IsAlwaysWindow(HWND window) const;

    bool IsAlwaysWindow(const std::wstring& executable, const std::wstring& title) const;

    static std::wstring FilePath();

private:
    static bool Identify(HWND window, DWORD& processId, DWORD& threadId);
    static std::wstring Normalize(const std::wstring& executable);

    bool MatchesAnyRule(const std::wstring& executable, const std::wstring& title) const;
    bool IsSuspended(HWND window) const;

    bool IsSuspended(HWND window, DWORD processId, DWORD threadId) const;

    void WriteTemplateFile(const std::wstring& path) const;

    static BOOL CALLBACK EnumProc(HWND window, LPARAM parameter);

    std::vector<Entry> entries_;
    std::vector<Rule> rules_;

    std::vector<Entry> suspended_;
};
