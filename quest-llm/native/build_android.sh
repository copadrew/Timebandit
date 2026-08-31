#!/usr/bin/env bash
#
# Cross-compiles libllama_unity.so (+ libwhisper_unity.so) for Quest 3 and drops
# them into unity/Assets/Plugins/Android/libs/arm64-v8a/.
#
# Requires: Android NDK r26 or newer, CMake 3.22+, Ninja, git.
#
#   ANDROID_NDK_HOME=~/Android/Sdk/ndk/26.3.11579264 ./build_android.sh
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="${HERE}/build-android-arm64"

NDK="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"
if [[ -z "${NDK}" || ! -d "${NDK}" ]]; then
    echo "error: set ANDROID_NDK_HOME to your NDK root (e.g. ~/Android/Sdk/ndk/26.3.11579264)" >&2
    exit 1
fi

TOOLCHAIN="${NDK}/build/cmake/android.toolchain.cmake"
if [[ ! -f "${TOOLCHAIN}" ]]; then
    echo "error: no android.toolchain.cmake under ${NDK} — is that really an NDK root?" >&2
    exit 1
fi

# API 29 is the floor for sched_setaffinity + the thermal API we use from C#.
# Quest 3 runs Android 12L (API 32); Meta's store currently requires targeting
# at least 32, which is a Unity Player Setting, not an NDK one.
API_LEVEL="${API_LEVEL:-29}"
BUILD_TYPE="${BUILD_TYPE:-Release}"
BUILD_WHISPER="${BUILD_WHISPER:-ON}"

echo "==> NDK        : ${NDK}"
echo "==> API level  : ${API_LEVEL}"
echo "==> Build type : ${BUILD_TYPE}"
echo "==> Whisper    : ${BUILD_WHISPER}"

cmake -S "${HERE}" -B "${BUILD_DIR}" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="${TOOLCHAIN}" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM="android-${API_LEVEL}" \
    -DANDROID_STL=c++_static \
    -DCMAKE_BUILD_TYPE="${BUILD_TYPE}" \
    -DLU_BUILD_WHISPER="${BUILD_WHISPER}" \
    "$@"

cmake --build "${BUILD_DIR}" --parallel "$(getconf _NPROCESSORS_ONLN)"

DEST="${HERE}/../unity/Assets/Plugins/Android/libs/arm64-v8a"
echo
echo "==> Built:"
ls -lh "${DEST}"/*.so 2>/dev/null || {
    echo "error: no .so landed in ${DEST}" >&2
    exit 1
}

# c++_static in more than one .so in the same process means two copies of the
# STL and, historically, some genuinely baffling crashes. We only get away with
# it because neither library passes STL types across its ABI boundary — both
# expose plain C. Worth re-checking if you ever widen these headers.
echo
echo "Reminder: both wrappers expose a plain C ABI on purpose. Do not add"
echo "std::string or std::vector to the public headers while ANDROID_STL is static."
