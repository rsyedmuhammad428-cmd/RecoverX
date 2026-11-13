cmake_minimum_required(VERSION 3.15)
project(RecoverXNative VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 14)            # conservative: builds with the VS 2015+ toolsets, which still target Windows 7
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(RX_SOURCES
    src/api.cpp
    src/fs_detect.cpp
    src/dest_guard.cpp
    src/ntfs_format.cpp
    src/file_sig.cpp
    src/ntfs_scan.cpp
    src/carve_format.cpp
    src/carve_scan.cpp
    src/fat_format.cpp
    src/fat_scan.cpp
    src/platform_win.cpp
    src/platform_stub.cpp)

# Absolute copy for the unit-test target, which compiles the engine sources directly so it can also test internals.
set(RX_TEST_SOURCES ${RX_SOURCES})
list(TRANSFORM RX_TEST_SOURCES PREPEND ${CMAKE_CURRENT_SOURCE_DIR}/)
set(RX_TEST_SOURCES ${RX_TEST_SOURCES} PARENT_SCOPE)
set(RX_INCLUDE_DIRS ${CMAKE_CURRENT_SOURCE_DIR}/include ${CMAKE_CURRENT_SOURCE_DIR}/src PARENT_SCOPE)

if(WIN32)
  list(APPEND RX_SOURCES src/rx_engine.rc)
endif()

add_library(recoverx_engine SHARED ${RX_SOURCES})
# C++ only: windres (MinGW resource compiler) cannot handle include paths that contain spaces, and the .rc needs none.
target_include_directories(recoverx_engine
  PUBLIC  $<BUILD_INTERFACE:$<$<COMPILE_LANGUAGE:CXX>:${CMAKE_CURRENT_SOURCE_DIR}/include>>
  PRIVATE $<$<COMPILE_LANGUAGE:CXX>:${CMAKE_CURRENT_SOURCE_DIR}/src>)
target_compile_definitions(recoverx_engine PRIVATE RX_ENGINE_BUILD)
set_target_properties(recoverx_engine PROPERTIES PREFIX "")   # recoverx_engine.dll on Windows

add_executable(rx_selftest src/selftest_main.cpp)
target_link_libraries(rx_selftest PRIVATE recoverx_engine)
target_include_directories(rx_selftest PRIVATE include)

if(WIN32)
  # Windows 7 SP1 baseline: expose only Windows 7 APIs, link the CRT statically so no VC++ redistributable is needed.
  target_compile_definitions(recoverx_engine PRIVATE _WIN32_WINNT=0x0601 WINVER=0x0601 UNICODE _UNICODE NOMINMAX)
  target_compile_definitions(rx_selftest PRIVATE _WIN32_WINNT=0x0601 WINVER=0x0601)
  if(MSVC)
    set_property(TARGET recoverx_engine rx_selftest PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_options(recoverx_engine PRIVATE /W4 /guard:cf- /GS)
    target_link_options(recoverx_engine PRIVATE /DYNAMICBASE /NXCOMPAT)
    target_link_options(rx_selftest PRIVATE /DYNAMICBASE /NXCOMPAT)
  else()
    target_link_options(recoverx_engine PRIVATE -static-libgcc -static-libstdc++ -static)
    target_link_options(rx_selftest PRIVATE -static-libgcc -static-libstdc++ -static)
  endif()
else()
  target_compile_options(recoverx_engine PRIVATE -Wall -Wextra)
endif()
