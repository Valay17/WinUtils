#pragma once

#include <windows.h>

#include <memory>
#include <utility>

namespace raii
{

template <typename T, typename Closer>
class Unique
{
public:
    Unique() = default;
    explicit Unique(T value) noexcept : value_(value) {}

    ~Unique() { Reset(); }

    Unique(const Unique&) = delete;
    Unique& operator=(const Unique&) = delete;

    Unique(Unique&& other) noexcept : value_(std::exchange(other.value_, Closer::kEmpty)) {}

    Unique& operator=(Unique&& other) noexcept
    {
        if (this != &other)
        {
            Reset();
            value_ = std::exchange(other.value_, Closer::kEmpty);
        }
        return *this;
    }

    [[nodiscard]] T Get() const noexcept { return value_; }
    [[nodiscard]] bool Valid() const noexcept { return value_ != Closer::kEmpty; }
    explicit operator bool() const noexcept { return Valid(); }

    void Reset(T value = Closer::kEmpty) noexcept
    {
        if (value_ != Closer::kEmpty)
        {
            Closer::Close(value_);
        }
        value_ = value;
    }

    [[nodiscard]] T Release() noexcept { return std::exchange(value_, Closer::kEmpty); }

private:
    T value_ = Closer::kEmpty;
};

struct HandleCloser
{
    static constexpr HANDLE kEmpty = nullptr;
    static void Close(HANDLE h) noexcept { CloseHandle(h); }
};

using UniqueHandle = Unique<HANDLE, HandleCloser>;

class OwnedMutex
{
public:
    OwnedMutex() = default;

    explicit OwnedMutex(HANDLE handle, bool owned) noexcept
        : handle_(handle), owned_(owned) {}

    ~OwnedMutex()
    {
        if (handle_ != nullptr)
        {
            if (owned_)
            {
                ReleaseMutex(handle_);
            }
            CloseHandle(handle_);
        }
    }

    OwnedMutex(const OwnedMutex&) = delete;
    OwnedMutex& operator=(const OwnedMutex&) = delete;

    [[nodiscard]] bool Valid() const noexcept { return handle_ != nullptr; }

private:
    HANDLE handle_ = nullptr;
    bool owned_ = false;
};

} // namespace raii
