#
# Builds libllama_unity.so and libwhisper_unity.so for arm64-v8a (Quest 3).
#
# Invoke through build_android.sh — it passes the NDK toolchain file and the
# ABI/platform settings this file assumes.
#
cmake_minimum_required(VERSION 3.22)
project(llama_unity CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Everything in the .so is hidden except the LU_API/WU_API entry points. Keeps
# llama.cpp's and whisper.cpp's symbols from leaking into the process and
# colliding with anything else Unity has loaded.
set(CMAKE_C_VISIBILITY_PRESET   hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

if(NOT ANDROID)
    message(WARNING "Configuring outside the NDK toolchain — the result will not run on Quest.")
endif()

if(ANDROID AND NOT ANDROID_ABI STREQUAL "arm64-v8a")
    message(FATAL_ERROR "Quest 3 is arm64-v8a only. Got ANDROID_ABI=${ANDROID_ABI}.")
endif()

# ---------------------------------------------------------------------------
# Upstream sources
#
# NOTE: both tags below are moving targets on their default branches. Pin them
# to the exact tag you tested and record it here — llama.cpp changes its public
# API without ceremony, and the compat:: block in src/llama_unity.cpp exists
# precisely because of that. If the build breaks on llama_model_load_from_file
# or llama_memory_clear, you are on an older tag: fix it there, in one place.
# ---------------------------------------------------------------------------
set(LLAMA_CPP_TAG   "master" CACHE STRING "llama.cpp git tag to build against")
set(WHISPER_CPP_TAG "master" CACHE STRING "whisper.cpp git tag to build against")
option(LU_BUILD_WHISPER "Build the whisper.cpp voice-input wrapper" ON)

include(FetchContent)

# --- llama.cpp -------------------------------------------------------------
set(LLAMA_BUILD_TESTS    OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_SERVER   OFF CACHE BOOL "" FORCE)
set(LLAMA_CURL           OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS    OFF CACHE BOOL "" FORCE)  # static-link into our .so

# GGML_NATIVE probes the *host* CPU. On a cross-compile that is nonsense and
# produces x86 flags for an ARM target, so it must be off; we set -march below.
set(GGML_NATIVE          OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP          OFF CACHE BOOL "" FORCE)  # NDK libomp is a packaging tax
set(GGML_LLAMAFILE       ON  CACHE BOOL "" FORCE)  # sgemm kernels, worth having
set(GGML_VULKAN          OFF CACHE BOOL "" FORCE)  # see README: GPU is the renderer's
set(GGML_OPENCL          OFF CACHE BOOL "" FORCE)

FetchContent_Declare(llama_cpp
    GIT_REPOSITORY https://github.com/ggml-org/llama.cpp.git
    GIT_TAG        ${LLAMA_CPP_TAG}
    GIT_SHALLOW    TRUE)
FetchContent_MakeAvailable(llama_cpp)

# --- our wrapper -----------------------------------------------------------
add_library(llama_unity SHARED src/llama_unity.cpp)

target_include_directories(llama_unity PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(llama_unity PRIVATE llama ggml)

# XR2 Gen 2 is a Snapdragon 8 Gen 2 derivative: ARMv8.2 with dotprod, i8mm and
# fp16 arithmetic. Those three flags are most of the int4 matmul performance on
# this chip — without them the Q4_K kernels fall back to scalar paths and you
# lose a large multiple of your tokens/sec. This also means the .so will SIGILL
# on pre-ARMv8.2 hardware, which is fine: the target is one headset.
target_compile_options(llama_unity PRIVATE
    -march=armv8.2-a+dotprod+i8mm+fp16
    -O3 -ffast-math -fno-finite-math-only -funroll-loops)

if(ANDROID)
    find_library(ANDROID_LOG_LIB log)
    target_link_libraries(llama_unity PRIVATE ${ANDROID_LOG_LIB})
endif()

# Strip in release. A shipped .so with llama.cpp's debug info is ~100 MB larger
# than it needs to be, and the APK has an install-size budget on the headset.
target_link_options(llama_unity PRIVATE
    $<$<CONFIG:Release>:-Wl,--strip-all>
    -Wl,--exclude-libs,ALL)

# --- whisper.cpp -----------------------------------------------------------
if(LU_BUILD_WHISPER)
    set(WHISPER_BUILD_TESTS    OFF CACHE BOOL "" FORCE)
    set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
    set(WHISPER_BUILD_SERVER   OFF CACHE BOOL "" FORCE)

    FetchContent_Declare(whisper_cpp
        GIT_REPOSITORY https://github.com/ggml-org/whisper.cpp.git
        GIT_TAG        ${WHISPER_CPP_TAG}
        GIT_SHALLOW    TRUE)
    FetchContent_MakeAvailable(whisper_cpp)

    add_library(whisper_unity SHARED src/whisper_unity.cpp)
    target_include_directories(whisper_unity PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
    target_link_libraries(whisper_unity PRIVATE whisper)
    target_compile_options(whisper_unity PRIVATE
        -march=armv8.2-a+dotprod+i8mm+fp16 -O3)
    if(ANDROID)
        target_link_libraries(whisper_unity PRIVATE ${ANDROID_LOG_LIB})
    endif()
    target_link_options(whisper_unity PRIVATE
        $<$<CONFIG:Release>:-Wl,--strip-all>
        -Wl,--exclude-libs,ALL)
endif()

# --- install straight into the Unity project -------------------------------
# Unity picks up any .so under Assets/Plugins/Android/libs/<abi>/ and packages
# it into the APK; no .meta authoring needed for the ABI to be inferred.
set(UNITY_PLUGIN_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/../unity/Assets/Plugins/Android/libs/arm64-v8a"
    CACHE PATH "Where to drop the built .so files")

add_custom_command(TARGET llama_unity POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGIN_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_FILE:llama_unity> "${UNITY_PLUGIN_DIR}/"
    COMMENT "Installing libllama_unity.so into the Unity project")

if(LU_BUILD_WHISPER)
    add_custom_command(TARGET whisper_unity POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                $<TARGET_FILE:whisper_unity> "${UNITY_PLUGIN_DIR}/"
        COMMENT "Installing libwhisper_unity.so into the Unity project")
endif()
