# quest-llm

An on-device LLM assistant for Meta Quest 3: llama.cpp compiled as an ARM64
shared library, bound into Unity through a plain C ABI, with on-device speech
input and a talking avatar. Nothing leaves the headset.

> **This directory is unrelated to the rest of this repository.** Timebandit is
> a single-file mobile web time clock served from GitHub Pages and reached by a
> printed QR poster. This subdirectory does not touch `index.html` or the Pages
> deploy, and the poster keeps working. It is here because it was asked for
> here, not because it belongs here.

## Layout

```
native/
  include/llama_unity.h      C ABI: load, tokenize, generate, free
  include/whisper_unity.h    C ABI for on-device dictation
  src/                       the wrappers
  CMakeLists.txt             fetches + cross-compiles llama.cpp / whisper.cpp
  build_android.sh           one-command NDK build
unity/Assets/
  Scripts/Native/            DllImport layer, no Unity types
  Scripts/Runtime/           MonoBehaviours: runner, voice, thermal, avatar, UI
  Editor/                    Tools ▸ Quest LLM ▸ Build Scene Rig
  Plugins/Android/           manifest + the built .so files land here
docs/DEPLOY.md               build, install, adb model push, troubleshooting
docs/MODELS.md               what actually fits in 8 GB, and why
tools/push_model.sh          adb deployment for the GGUF
```

Start with `docs/DEPLOY.md`.

## The four decisions that shape all of this

Most of the code here is ordinary. These four choices are the ones that would be
expensive to reverse later, so they are worth stating plainly.

### 1. The model is small, because the memory ceiling is real

An 8 GB headset that is also rendering stereo at 72 Hz leaves roughly 3.5 GB for
weights. A 7B at Q4 is 4.1 GB before the KV cache. **3B is the ceiling, not the
compromise.** Full arithmetic in `docs/MODELS.md`, including the KV cache cost
that people size a model and then forget about.

### 2. Inference is pinned off the render thread's cores

"Run it on a background thread" is not sufficient on this device. A background
thread still competes for cores, and on Quest, dropping frames is not a
performance nit — it is motion sickness, and it fails store review.

So the worker thread is pinned with `sched_setaffinity` to a configurable core
mask (`0x70`, cores 4–6, by default), leaving the prime core and the little
cores for Unity's render thread and the compositor. That default is a starting
guess. Measure it — the status line shows live fps and tok/s for exactly this.

### 3. Thermals are managed, not hoped for

This is the constraint that sinks most projects of this shape, and the original
spec did not mention it. A Quest 3 is a passively-cooled SoC strapped to a face.
Saturating cores with int4 matmuls is a sustained all-core load the thermal
design does not budget for, and the device's response is not to politely slow
your inference — it throttles the whole SoC, compositor included.

`ThermalGovernor` therefore throttles *us* first, on two signals: Android's
`PowerManager.getCurrentThermalStatus()`, and measured frame time. It inserts a
growing per-token sleep, and cancels generation outright at `THERMAL_STATUS_SEVERE`.
Frame time usually trips first, which is the point — by the time the thermal API
complains you have already shipped judder.

Expect tokens/sec to sag during a long session. That is the governor working.

### 4. No native → managed callbacks

The obvious streaming design is a function pointer from C++ into C#. Under
IL2CPP on Android that requires the native thread to be attached, the target to
be a static `[MonoPInvokeCallback]`, and it still may not touch any Unity API —
and the failure mode is a hard crash in a stack you cannot read.

Instead the native side accumulates bytes in a mutex-guarded queue and
`LlamaRunner.Update()` drains it once per frame into a pre-allocated buffer,
capped at `maxBytesPerFrame`. `lu_poll()` never splits a UTF-8 sequence across
calls, so each chunk decodes independently. One call per frame, no marshalling
landmines, no crash class.

## Why the GPU is not used

`n_gpu_layers` is 0 and the Vulkan/OpenCL backends are off in `CMakeLists.txt`.
This is deliberate. On a headset the GPU is not idle hardware waiting to be
exploited — it is drawing two eye buffers every 13.9 ms, and the compositor is
doing timewarp on top of that. Handing llama.cpp a Vulkan queue on this device
trades a CPU problem you can pin and throttle for a GPU problem that lands
directly on frame time.

The Hexagon NPU would be the genuinely right answer for this workload and needs
Qualcomm's QNN SDK, which llama.cpp does not target. That is the real
performance ceiling here, and it is not reachable from this architecture.

## Voice input

`whisper.cpp` as a second native library, `ggml-base.en-q5_1` (~60 MB), pinned to
a disjoint core set from the LLM.

Meta's Voice SDK (Wit.ai) is the easier path and it is a **cloud** service. If
the point of the project is that nothing leaves the headset, dictation has to be
local too.

Capture is push-to-talk with an energy VAD and a trailing-silence timeout.
Whisper is not fed streaming slices — it is an encoder-decoder over a fixed 30 s
window and produces confident nonsense from 200 ms fragments. One utterance, one
transcription. Transcription and generation are serialised in `ChatPanel`: the
cores and the thermal envelope are shared even though the affinity masks are not.

## The avatar

`AvatarController` drives gaze, blink, idle sway and jaw motion procedurally —
no model, microseconds per frame. It works with primitives out of the box (the
scene builder makes a sphere with two eyes and a mouth) and switches to
blendshapes by setting `faceRenderer` plus two indices.

Speech output goes through Android's **system** `TextToSpeech` service rather
than a neural TTS. Piper or Kokoro would sound better and would be a third model
competing for a CPU budget that is already oversubscribed. The system engine
runs in its own process and costs this app no worker threads. Replies are spoken
sentence-at-a-time as they stream, because at single-digit tokens/sec waiting
for a complete reply means seconds of silence staring at a still face.

Lip sync is procedural at a plausible syllable rate, not phoneme-accurate: the
Android TTS service will not hand back the synthesised buffer without writing it
to a file and re-reading it. At conversational distance the difference is not
visible, and if the headset has no voice data installed the avatar still
animates off token arrival.

## Honest limitations

- **A 3B model is a 3B model.** It will be confidently wrong. This is a nice
  demo and a genuinely private assistant; it is not a good research tool.
- **Sustained generation warms the headset.** The governor keeps the frame rate,
  not your comfort.
- **`adb push` for the model means this cannot ship on the store as-is.** Store
  distribution needs an in-app download with a progress UI and a resume path.
- **Untested against a pinned upstream.** Both `CMakeLists.txt` tags default to
  `master`. Pin them once you have a green build.
