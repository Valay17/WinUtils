#pragma once

#include <windows.h>

class Inverter
{
public:
    ~Inverter();

    bool Initialize();
    void Shutdown();

    void SetInverted(bool inverted);
    bool IsInverted() const { return inverted_; }

    bool HasMagnifier() const { return ownsMagnifier_; }
    bool OtherMagnifierUserPresent() const { return otherMagnifierUserPresent_; }

private:
    bool Apply(bool inverted);

    bool initialized_ = false;
    bool ownsMagnifier_ = false;
    bool otherMagnifierUserPresent_ = false;
    bool inverted_ = false;
    HANDLE magnifierMutex_ = nullptr;
};
