cmake_minimum_required(VERSION 3.20)
project(DesktopZoom LANGUAGES CXX RC)

#
# Desktop Zoom - Fullscreen Zoom (keyboard-driven)
#
# Cursor-centered fullscreen zoom via MagSetFullscreenTransform, driven by three
# hotkeys. Touchpad pinch gestures over raw HID are not built, so nothing here
# touches RegisterRawInputDevices.
#
# Everything lives beside the executable: DesktopZoom.log only. No config.ini -
# every setting is a Settings.h constant instead, see Settings.cpp - no
# %APPDATA%, no %TEMP%, no registry.
#
# Build on Windows:
#     cmake -S . -B build
#     cmake --build build --config Release
#
# Output: build/Release/DesktopZoom.exe - a single executable, no
# redistributable.
#

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(POLICY CMP0091)
    cmake_policy(SET CMP0091 NEW)
endif()

# The .rc carries the tray icon and the application manifest. Both used to be
# absent: the icon was drawn with GDI at run time, and with no manifest the
# process was treated as designed for Windows 8 and got compatibility-shimmed
# API values.
add_executable(DesktopZoom WIN32
    DesktopZoom.rc
    main.cpp
    ZoomRenderer.cpp
    Settings.cpp
)

target_compile_definitions(DesktopZoom PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX

    # Windows 10 target, this project's platform floor.
    _WIN32_WINNT=0x0A00
    NTDDI_VERSION=0x0A000000
    WINVER=0x0A00
)

# Magnification.dll is deliberately NOT linked - ZoomRenderer.cpp resolves its
# three entry points with LoadLibrary/GetProcAddress at run time. See the comment
# there for why.
#
# gdi32 is gone: nothing is drawn at run time any more. The tray icon is an
# embedded resource loaded once with LoadIcon.
#
# user32:  hotkeys, window, LoadIcon, Shell_NotifyIcon's callback message
# shell32: tray icon
# advapi32: the Run key, for "Start With Windows"
target_link_libraries(DesktopZoom PRIVATE
    user32
    shell32
    advapi32
)

if(MSVC)
    target_compile_options(DesktopZoom PRIVATE /W4 /permissive-)

    # /MANIFEST:NO - required now that the manifest is embedded through
    # DesktopZoom.rc.
    #
    # MSVC's linker generates a default manifest of its own and embeds it as
    # resource type MANIFEST, id 1. The .rc embeds one at the same type and id,
    # so the resource compiler sees two and the link dies:
    #
    #   CVTRES : fatal error CVT1100: duplicate resource. type:MANIFEST, name:1
    #   LINK   : fatal error LNK1123: failure during conversion to COFF
    #
    # This says "do not generate one", leaving the .rc as the single source.
    #
    # A non-Windows type-check with mingw won't catch this: its linker adds no
    # default manifest, so there's nothing to collide with - a real limit of
    # that cross-check, worth building on real Windows before calling it done.
    target_link_options(DesktopZoom PRIVATE /MANIFEST:NO)

    # Static CRT so the executable needs no redistributable runtime installed.
    set_property(TARGET DesktopZoom PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
    # mingw path - type checking only, never shipped.
    target_compile_options(DesktopZoom PRIVATE -Wall -Wextra)
    target_link_options(DesktopZoom PRIVATE -static -municode)
endif()
