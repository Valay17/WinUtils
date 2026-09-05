cmake_minimum_required(VERSION 3.20)
project(MonitorBrightness LANGUAGES CXX RC)

#
# Monitor Brightness - External Monitor Brightness (DDC/CI)
#
# Controls the brightness of external monitors over DDC/CI. The laptop's internal
# panel is deliberately out of scope - Windows already drives that natively - and
# is listed as unsupported rather than hidden.
#
# Everything lives beside the executable: config.ini and MonitorBrightness.log.
# No %APPDATA%, no %TEMP%, no registry beyond the opt-in startup entry.
#
# Build on Windows:
#     cmake -S . -B build
#     cmake --build build --config Release
#
# Output: build/Release/MonitorBrightness.exe - a single executable, no
# redistributable.
#

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(POLICY CMP0091)
    cmake_policy(SET CMP0091 NEW)
endif()

add_executable(MonitorBrightness WIN32
    MonitorBrightness.rc
    main.cpp
    MonitorControl.cpp
    Config.cpp
    SliderPopup.cpp
    CustomValuePopup.cpp
)

target_compile_definitions(MonitorBrightness PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX

    # Windows 10 target, this project's platform floor.
    _WIN32_WINNT=0x0A00
    NTDDI_VERSION=0x0A000000
    WINVER=0x0A00
)

# Global hotkeys (Ctrl+Alt+PageUp/PageDown) are disabled by default - main.cpp's
# own MONITORBRIGHTNESS_ENABLE_HOTKEYS guard covers the code; this is what actually lets
# `cmake -S . -B build -DMONITORBRIGHTNESS_ENABLE_HOTKEYS=1` turn it back on without editing
# main.cpp directly.
option(MONITORBRIGHTNESS_ENABLE_HOTKEYS "Register the global brightness hotkeys" OFF)
if(MONITORBRIGHTNESS_ENABLE_HOTKEYS)
    target_compile_definitions(MonitorBrightness PRIVATE MONITORBRIGHTNESS_ENABLE_HOTKEYS=1)
endif()

# dxva2:    GetPhysicalMonitorsFromHMONITOR, Get/SetMonitorBrightness - the
#           whole DDC/CI surface lives here, not in user32.
# user32:   monitor enumeration, hotkeys, window, device notifications
# shell32:  tray icon, ShellExecute
# comctl32: the slider popup's trackbar control - and gdi32 comes back with
#           it, for the popup's own custom-drawn channel/thumb and border.
# dbghelp:  MiniDumpWriteDump - the crash-dump writer installed in wWinMain,
#           a standard, always-present Windows system DLL.
target_link_libraries(MonitorBrightness PRIVATE
    dxva2
    user32
    shell32
    comctl32
    gdi32
    dbghelp
)

if(MSVC)
    target_compile_options(MonitorBrightness PRIVATE /W4 /permissive-)

    # /MANIFEST:NO - required now that the manifest is embedded through
    # MonitorBrightness.rc. MSVC's linker generates its own default manifest
    # unless told not to, and the .rc already embeds one at the same resource
    # type/id - two would collide (CVT1100 duplicate resource, LNK1123).
    target_link_options(MonitorBrightness PRIVATE /MANIFEST:NO)

    # Static CRT so the executable needs no redistributable runtime installed.
    set_property(TARGET MonitorBrightness PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
    # mingw path - type checking only, never shipped.
    target_compile_options(MonitorBrightness PRIVATE -Wall -Wextra)
    target_link_options(MonitorBrightness PRIVATE -static -municode)
endif()
