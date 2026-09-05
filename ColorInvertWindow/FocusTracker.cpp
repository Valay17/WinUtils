#include "FocusTracker.h"

#include <psapi.h>

#include <vector>

namespace
{

FocusTracker::Callback g_callback;

} // namespace

FocusTracker::~FocusTracker()
{
    Stop();
}

bool FocusTracker::Start(Callback callback)
{
    if (hook_ != nullptr)
    {
        return true;
    }

    g_callback = std::move(callback);

    hook_ = SetWinEventHook(
        EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
        nullptr, &FocusTracker::HookProc,
        0, 0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

    if (hook_ == nullptr)
    {
        g_callback = nullptr;
        return false;
    }

    return true;
}

void FocusTracker::Stop()
{
    if (hook_ != nullptr)
    {
        UnhookWinEvent(hook_);
        hook_ = nullptr;
    }

    g_callback = nullptr;
}

void CALLBACK FocusTracker::HookProc(
    HWINEVENTHOOK /*hook*/, DWORD event, HWND window,
    LONG objectId, LONG /*childId*/, DWORD /*threadId*/, DWORD /*timestamp*/)
{
    if (event != EVENT_SYSTEM_FOREGROUND || window == nullptr || objectId != OBJID_WINDOW)
    {
        return;
    }

    if (g_callback)
    {
        g_callback(window, ExecutableForWindow(window), ClassForWindow(window));
    }
}

std::wstring FocusTracker::TitleForWindow(HWND window, size_t maxLength)
{
    if (window == nullptr)
    {
        return {};
    }

    const int length = GetWindowTextLengthW(window);
    if (length <= 0)
    {
        return {};
    }

    std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
    const int copied = GetWindowTextW(window, buffer.data(), static_cast<int>(buffer.size()));
    if (copied <= 0)
    {
        return {};
    }

    std::wstring title(buffer.data(), static_cast<size_t>(copied));

    if (maxLength > 3 && title.size() > maxLength)
    {
        title.resize(maxLength - 3);
        title += L"...";
    }

    return title;
}

std::wstring FocusTracker::ClassForWindow(HWND window)
{
    if (window == nullptr)
    {
        return {};
    }

    // 256 is the documented maximum length of a registered window class name.
    wchar_t buffer[256] = {};
    const int length = GetClassNameW(window, buffer, ARRAYSIZE(buffer));

    return length > 0 ? std::wstring(buffer, length) : std::wstring();
}

std::wstring FocusTracker::ExecutableForWindow(HWND window)
{
    if (window == nullptr)
    {
        return {};
    }

    DWORD processId = 0;
    if (GetWindowThreadProcessId(window, &processId) == 0 || processId == 0)
    {
        return {};
    }

    const HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (process == nullptr)
    {
        return {};
    }

    std::vector<wchar_t> buffer(MAX_PATH);
    DWORD length = static_cast<DWORD>(buffer.size());

    if (!QueryFullProcessImageNameW(process, 0, buffer.data(), &length))
    {
        buffer.resize(32768);
        length = static_cast<DWORD>(buffer.size());

        if (!QueryFullProcessImageNameW(process, 0, buffer.data(), &length))
        {
            CloseHandle(process);
            return {};
        }
    }

    CloseHandle(process);

    const std::wstring fullPath(buffer.data(), length);
    const size_t separator = fullPath.find_last_of(L'\\');

    return separator == std::wstring::npos ? fullPath : fullPath.substr(separator + 1);
}
