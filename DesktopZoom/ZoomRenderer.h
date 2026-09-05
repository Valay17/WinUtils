#pragma once

#include <windows.h>

class ZoomRenderer
{
public:
    ~ZoomRenderer();

    bool Initialize();
    void Shutdown();

    float Adjust(float delta, float maxZoom);
    void Reset();

    float Level() const { return level_; }
    bool IsZoomed() const { return level_ > 1.001f; }
    bool Available() const { return initialized_; }

    bool OtherMagnifierUserPresent() const { return otherMagnifierUserPresent_; }

    void RefreshScreenMetrics();

private:
    bool Apply(float level, POINT pointer, bool quiet);
    void ComputeOrigin(float level, POINT pointer, int& x, int& y) const;  // origin = pointer * (1 - 1/level)

    void StartFollowing();
    void StopFollowing();

    static void CALLBACK PointerHookProc(
        HWINEVENTHOOK hook, DWORD event, HWND window,
        LONG objectId, LONG childId, DWORD threadId, DWORD timestamp);

    void OnPointerMoved();

    bool initialized_ = false;
    bool otherMagnifierUserPresent_ = false;
    float level_ = 1.0f;
    HANDLE marker_ = nullptr;

    HWINEVENTHOOK pointerHook_ = nullptr;

    int screenLeft_ = 0;
    int screenTop_ = 0;
    int screenWidth_ = 0;
    int screenHeight_ = 0;

    int appliedX_ = 0;
    int appliedY_ = 0;
};
