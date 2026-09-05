#pragma once

#include <windows.h>

#include <functional>
#include <string>

class FocusTracker
{
public:
    using Callback = std::function<void(
        HWND window, const std::wstring& executable, const std::wstring& windowClass)>;

    ~FocusTracker();

    bool Start(Callback callback);
    void Stop();

    static std::wstring ExecutableForWindow(HWND window);
    static std::wstring ClassForWindow(HWND window);
    static std::wstring TitleForWindow(HWND window, size_t maxLength = 40);

private:
    static void CALLBACK HookProc(
        HWINEVENTHOOK hook, DWORD event, HWND window,
        LONG objectId, LONG childId, DWORD threadId, DWORD timestamp);

    HWINEVENTHOOK hook_ = nullptr;
};
