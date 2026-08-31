# Building and deploying

## 0. Prerequisites

- Unity 2022.3 LTS or Unity 6 with the **Android Build Support** module
  (including the OpenJDK and SDK/NDK sub-modules).
- Android NDK **r26+**. Unity ships one; you can point at it or install your own.
- CMake 3.22+, Ninja, git, and `adb` on your PATH.
- Meta Quest 3 or 3S in **Developer Mode**, with a developer account and the
  Meta Quest Developer Hub (or just `adb`).

## 1. Build the native libraries

```bash
cd quest-llm/native
export ANDROID_NDK_HOME=~/Android/Sdk/ndk/26.3.11579264   # adjust
./build_android.sh
```

This fetches llama.cpp and whisper.cpp, cross-compiles for `arm64-v8a`, and
copies the result into
`quest-llm/unity/Assets/Plugins/Android/libs/arm64-v8a/`:

- `libllama_unity.so`
- `libwhisper_unity.so`

**Pin your upstream tags.** `CMakeLists.txt` defaults both to `master`, which
means a rebuild months from now may fetch a llama.cpp whose API has moved. When
you have a build that works, set `LLAMA_CPP_TAG` / `WHISPER_CPP_TAG` to the exact
tags and commit that. The `compat::` block at the top of `src/llama_unity.cpp` is
the single place to adapt if you land on an older API.

Confirm the ABI before you waste a build cycle:

```bash
file unity/Assets/Plugins/Android/libs/arm64-v8a/libllama_unity.so
# ...ELF 64-bit LSB shared object, ARM aarch64...
```

## 2. Unity project settings

Create a project (3D URP is fine), copy the `Assets` tree from
`quest-llm/unity/Assets/` into it, then:

**Player Settings ▸ Other Settings**

| Setting | Value | Why |
|---|---|---|
| Scripting Backend | **IL2CPP** | Mono has no ARM64 Android support |
| Target Architectures | **ARM64 only** | Quest is ARM64; ARMv7 doubles build time for nothing |
| Minimum API Level | **29** | `sched_setaffinity`, thermal API |
| Target API Level | **32+** | Meta store requirement |
| Internet Access | Not Required | nothing here talks to a network |
| Write Permission | External (SDCard) | not strictly needed for scoped storage, but harmless |

**Player Settings ▸ Publishing Settings** — leave *Custom Main Manifest*
unchecked; the file at `Assets/Plugins/Android/AndroidManifest.xml` is picked up
automatically.

**XR Plug-in Management** — install the Oculus/Meta XR plugin and tick it under
the Android tab. Set the display refresh rate to match `ThermalGovernor.targetHz`
(72 Hz is the safe default for a CPU-heavy app; do not reach for 120 Hz here).

Then import TMP essentials (*Window ▸ TextMeshPro ▸ Import TMP Essential
Resources*) and run **Tools ▸ Quest LLM ▸ Build Scene Rig**. That constructs the
panel, the avatar and all the wiring.

## 3. Build and install the APK

```
File ▸ Build Settings ▸ Android ▸ Build And Run
```

Or build the APK and:

```bash
adb install -r questllm.apk
```

## 4. Push the models

The GGUF is deliberately **not** shipped inside the APK. A 2 GB `StreamingAssets`
payload makes every iteration a multi-minute reinstall, and Meta's store has an
APK size limit you would blow through immediately.

**Launch the app once before pushing.** Android only creates
`/sdcard/Android/data/<pkg>/files` when the app first runs, and `adb` cannot
create it for you — this is the step that produces the confusing
"Permission denied" everybody hits.

```bash
cd quest-llm/tools
./push_model.sh ~/models/Llama-3.2-3B-Instruct-abliterated-Q4_K_M.gguf \
                ~/models/ggml-base.en-q5_1.bin
```

Which is a wrapper over:

```bash
PKG=com.dclabs.questllm
adb shell mkdir -p /sdcard/Android/data/$PKG/files/models
adb push model.gguf            /sdcard/Android/data/$PKG/files/models/model.gguf
adb push ggml-base.en-q5_1.bin /sdcard/Android/data/$PKG/files/models/
adb shell ls -lh               /sdcard/Android/data/$PKG/files/models
```

A 2 GB push takes a few minutes on USB 2 and well under one on USB 3. If it is
crawling, you are on the charging cable.

## 5. Watch it run

```bash
adb logcat -c
adb logcat -s Unity:V llama_unity:V whisper_unity:V AndroidRuntime:E
```

You should see `loaded '<path>' (n_ctx=2048 threads=3 mask=0x70)`.

## Troubleshooting

**`DllNotFoundException: llama_unity`**
The `.so` did not make it into the APK. Check it is under
`Assets/Plugins/Android/libs/arm64-v8a/`, that Target Architectures is ARM64, and
unzip the APK to confirm: `unzip -l app.apk | grep llama_unity`.

**App dies silently a few seconds after launch**
Almost always OOM. Check with `adb shell dumpsys meminfo <pkg>` and drop to a
smaller quant or a smaller `contextTokens`. See `MODELS.md`.

**`SIGILL` on load**
The native libraries are compiled with `-march=armv8.2-a+dotprod+i8mm`. That is
correct for Quest 3 and will crash on older hardware. Lower the `-march` in
`CMakeLists.txt` if you genuinely need Quest 2, and expect to lose a lot of
throughput.

**Frame rate tanks while generating**
Lower `threads`, or change `cpuMask` — the 0x70 default is a starting guess, not
a measurement. Watch the `fps` field in the panel's status line while you tune.

**Model loads but replies are nonsense**
Chat template. See the end of `MODELS.md`.

**Microphone does nothing**
`Microphone.devices` is empty until `RECORD_AUDIO` is granted. The permission
prompt appears on first launch; if it was dismissed, re-grant it with
`adb shell pm grant <pkg> android.permission.RECORD_AUDIO`.

**Avatar never speaks**
The headset may have no TTS voice data for `en-US`. `AndroidTts` logs
`no voice data for en-US` and the avatar falls back to silent mouth movement.
That is a supported state, not a failure.
