#!/usr/bin/env bash
#
# Pushes a GGUF (and optionally a whisper model) into the app's persistent data
# directory on a connected Quest 3.
#
#   ./push_model.sh model.gguf
#   ./push_model.sh model.gguf ggml-base.en-q5_1.bin
#
# The app must have been launched at least once first — Android creates
# /sdcard/Android/data/<pkg>/files only when the app runs, and adb cannot mkdir
# into another app's scoped-storage directory before it exists.
#
set -euo pipefail

PKG="${PKG:-com.dclabs.questllm}"
DEST="/sdcard/Android/data/${PKG}/files/models"

MODEL="${1:-}"
WHISPER="${2:-}"

if [[ -z "${MODEL}" ]]; then
    echo "usage: $0 <model.gguf> [whisper-model.bin]" >&2
    exit 1
fi
if [[ ! -f "${MODEL}" ]]; then
    echo "error: no such file: ${MODEL}" >&2
    exit 1
fi

if ! command -v adb >/dev/null 2>&1; then
    echo "error: adb not on PATH (Android platform-tools)" >&2
    exit 1
fi

devices=$(adb devices | awk 'NR>1 && $2=="device" {print $1}')
count=$(printf '%s\n' "${devices}" | grep -c . || true)
if [[ "${count}" -eq 0 ]]; then
    echo "error: no authorised device. Put the headset on and accept the USB debugging prompt." >&2
    exit 1
elif [[ "${count}" -gt 1 ]]; then
    echo "error: more than one device attached; set ANDROID_SERIAL to pick one:" >&2
    printf '  %s\n' ${devices} >&2
    exit 1
fi

# Fail early and clearly rather than letting adb push emit a bare "Permission
# denied", which is the single most confusing part of this workflow.
if ! adb shell "[ -d /sdcard/Android/data/${PKG}/files ]" 2>/dev/null; then
    echo "error: /sdcard/Android/data/${PKG}/files does not exist." >&2
    echo "       Install and LAUNCH the app once, then re-run this script." >&2
    exit 1
fi

adb shell mkdir -p "${DEST}"

size=$(du -h "${MODEL}" | cut -f1)
echo "==> pushing ${MODEL} (${size}) to ${DEST}/model.gguf"
echo "    a multi-gigabyte push over USB 2 takes several minutes; if it crawls,"
echo "    check you are on the USB 3 cable and port."
time adb push "${MODEL}" "${DEST}/model.gguf"

if [[ -n "${WHISPER}" ]]; then
    if [[ ! -f "${WHISPER}" ]]; then
        echo "error: no such file: ${WHISPER}" >&2
        exit 1
    fi
    echo "==> pushing ${WHISPER} to ${DEST}/$(basename "${WHISPER}")"
    adb push "${WHISPER}" "${DEST}/$(basename "${WHISPER}")"
fi

echo
echo "==> on-device contents:"
adb shell ls -lh "${DEST}"

echo
echo "Done. Watch it load with:"
echo "  adb logcat -s Unity:V llama_unity:V whisper_unity:V"
