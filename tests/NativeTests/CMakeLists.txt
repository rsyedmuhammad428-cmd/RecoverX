add_executable(native_tests native_tests.cpp ${RX_TEST_SOURCES})
target_include_directories(native_tests PRIVATE ${RX_INCLUDE_DIRS})
target_compile_definitions(native_tests PRIVATE RX_ENGINE_BUILD)

add_executable(ntfs_tests ntfs_tests.cpp ${RX_TEST_SOURCES})
target_include_directories(ntfs_tests PRIVATE ${RX_INCLUDE_DIRS})
target_compile_definitions(ntfs_tests PRIVATE RX_ENGINE_BUILD)

add_executable(carve_tests carve_tests.cpp ${RX_TEST_SOURCES})
target_include_directories(carve_tests PRIVATE ${RX_INCLUDE_DIRS})
target_compile_definitions(carve_tests PRIVATE RX_ENGINE_BUILD)

add_executable(fat_tests fat_tests.cpp ${RX_TEST_SOURCES})
target_include_directories(fat_tests PRIVATE ${RX_INCLUDE_DIRS})
target_compile_definitions(fat_tests PRIVATE RX_ENGINE_BUILD)

# Test executables compile the engine sources themselves; RX_ALLOW_IMAGE_FILES lets them open disk-image files as volumes
# on Windows too (the shipped DLL never gets this define).
foreach(t native_tests ntfs_tests carve_tests fat_tests)
  if(WIN32)
    target_compile_definitions(${t} PRIVATE _WIN32_WINNT=0x0601 WINVER=0x0601 NOMINMAX RX_ALLOW_IMAGE_FILES)
  endif()
  if(WIN32 AND NOT MSVC)
    target_link_options(${t} PRIVATE -static-libgcc -static-libstdc++ -static)
  endif()
  if(MSVC)
    set_property(TARGET ${t} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  endif()
endforeach()

if(WIN32)
  add_executable(abi_layout_check abi_layout_check.cpp)          # compile-time asserts of the C# struct layouts
  target_include_directories(abi_layout_check PRIVATE ${RX_INCLUDE_DIRS})
endif()

add_test(NAME native_tests COMMAND native_tests)

# Real-NTFS fixture: a 64 MB volume built with the NTFS driver (see tests/fixtures/make_ntfs_fixture.py), unpacked at
# configure time with CMake's own tar so it works on every OS. Volume tests run on non-Windows hosts.
set(RX_FIXTURE_SRC ${CMAKE_SOURCE_DIR}/tests/fixtures)
set(RX_FIXTURE_OUT ${CMAKE_BINARY_DIR}/fixtures)
if(NOT EXISTS ${RX_FIXTURE_OUT}/ntfs_basic.img)
  file(MAKE_DIRECTORY ${RX_FIXTURE_OUT})
  execute_process(COMMAND ${CMAKE_COMMAND} -E tar xzf ${RX_FIXTURE_SRC}/ntfs_basic.tar.gz WORKING_DIRECTORY ${RX_FIXTURE_OUT})
endif()
configure_file(${RX_FIXTURE_SRC}/ntfs_basic.manifest.tsv ${RX_FIXTURE_OUT}/ntfs_basic.manifest.tsv COPYONLY)
target_compile_definitions(ntfs_tests PRIVATE RX_FIXTURE_DIR="${RX_FIXTURE_OUT}")
target_compile_definitions(carve_tests PRIVATE RX_FIXTURE_DIR="${RX_FIXTURE_OUT}")
target_compile_definitions(fat_tests PRIVATE RX_FIXTURE_DIR="${RX_FIXTURE_OUT}")
if(NOT EXISTS ${RX_FIXTURE_OUT}/fat32_basic.img)
  execute_process(COMMAND ${CMAKE_COMMAND} -E tar xzf ${RX_FIXTURE_SRC}/fat_fixtures.tar.gz WORKING_DIRECTORY ${RX_FIXTURE_OUT})
endif()
configure_file(${RX_FIXTURE_SRC}/fat32_basic.manifest.tsv ${RX_FIXTURE_OUT}/fat32_basic.manifest.tsv COPYONLY)
configure_file(${RX_FIXTURE_SRC}/exfat_basic.manifest.tsv ${RX_FIXTURE_OUT}/exfat_basic.manifest.tsv COPYONLY)
if(NOT EXISTS ${RX_FIXTURE_OUT}/carve_basic.img)
  execute_process(COMMAND ${CMAKE_COMMAND} -E tar xzf ${RX_FIXTURE_SRC}/carve_basic.tar.gz WORKING_DIRECTORY ${RX_FIXTURE_OUT})
endif()
configure_file(${RX_FIXTURE_SRC}/carve_basic.manifest.tsv ${RX_FIXTURE_OUT}/carve_basic.manifest.tsv COPYONLY)
set_tests_properties(native_tests PROPERTIES TIMEOUT 120)
add_test(NAME ntfs_tests COMMAND ntfs_tests)
set_tests_properties(ntfs_tests PROPERTIES TIMEOUT 900)
add_test(NAME carve_tests COMMAND carve_tests)
add_test(NAME fat_tests COMMAND fat_tests)
set_tests_properties(fat_tests PROPERTIES TIMEOUT 900)
set_tests_properties(carve_tests PROPERTIES TIMEOUT 900)

# Large-volume / performance check (Windows, run manually: perf_tests [size_gb]). Not part of ctest: it needs ~16 GB of
# sparse-file address space in %TEMP% and takes a while.
if(WIN32)
  add_executable(perf_tests perf_tests.cpp ${RX_TEST_SOURCES})
  target_include_directories(perf_tests PRIVATE ${RX_INCLUDE_DIRS})
  target_compile_definitions(perf_tests PRIVATE RX_ENGINE_BUILD _WIN32_WINNT=0x0601 WINVER=0x0601 NOMINMAX RX_ALLOW_IMAGE_FILES RX_FIXTURE_DIR="${RX_FIXTURE_OUT}")
  target_link_libraries(perf_tests PRIVATE psapi)
  if(NOT MSVC)
    target_link_options(perf_tests PRIVATE -static-libgcc -static-libstdc++ -static)
  endif()
endif()
