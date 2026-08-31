// LlamaRunner.cs — owns the native context and pumps streamed tokens onto the
// main thread without ever blocking the render loop.
//
// The whole reason this class is shaped the way it is: on Quest, dropping below
// the display's refresh rate is not a performance nit, it is motion sickness,
// and Meta's store review rejects builds that do it. So:
//
//   * All decoding happens on a native worker thread we never join per-frame.
//   * Update() does one non-blocking poll into a pre-allocated buffer and
//     stops after MaxBytesPerFrame, so a burst of buffered tokens cannot turn
//     into a string-building spike inside one frame.
//   * Nothing here allocates per token except the decoded chunk string itself.

using System;
using System.Collections;
using System.IO;
using System.Text;
using QuestLlm.Native;
using UnityEngine;

namespace QuestLlm.Runtime
{
    [DisallowMultipleComponent]
    public sealed class LlamaRunner : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("Path relative to Application.persistentDataPath. Push the GGUF " +
                 "there with tools/push_model.sh — it is not shipped in the APK.")]
        public string modelRelativePath = "models/model.gguf";

        [TextArea(3, 8)]
        public string systemPrompt =
            "You are a concise assistant running entirely on this headset. " +
            "Answer in at most a few sentences unless asked for more.";

        [Header("Context and sampling")]
        [Tooltip("KV window. Every 1024 tokens here costs real megabytes; 2048 " +
                 "is usually the right trade on an 8 GB headset.")]
        public int contextTokens = 2048;
        public int maxResponseTokens = 256;
        [Range(0f, 2f)] public float temperature = 0.7f;
        [Range(0f, 1f)] public float topP = 0.92f;
        public int topK = 40;
        [Range(1f, 2f)] public float repeatPenalty = 1.08f;

        [Header("Scheduling")]
        [Tooltip("Worker threads. Do not exceed the number of cores in cpuMask.")]
        public int threads = 3;

        [Tooltip("Affinity mask, bit N = CPU N. 0x70 is cores 4-6, which leaves " +
                 "the prime core and the little cores to Unity and the compositor. " +
                 "0 disables pinning. Measure before trusting the default.")]
        public uint cpuMask = 0x70;

        [Tooltip("Bytes of streamed text drained per frame. Caps the worst-case " +
                 "main-thread cost of a token burst.")]
        public int maxBytesPerFrame = 512;

        [Header("Diagnostics")]
        public bool logLlamaCppToLogcat = false;

        // --- events (raised on the main thread only) ------------------------
        public event Action<string> OnToken;        // incremental chunk
        public event Action<string> OnComplete;     // full reply
        public event Action<string> OnError;
        public event Action OnLoaded;

        public bool IsLoaded    => _ctx != IntPtr.Zero;
        public bool IsGenerating => _lastStatus == LuStatus.Running;
        public float PromptTokensPerSecond { get; private set; }
        public float GenTokensPerSecond    { get; private set; }
        public int   ContextUsed           { get; private set; }
        public int   ContextSize           { get; private set; }

        private IntPtr _ctx = IntPtr.Zero;
        private byte[] _pollBuffer;
        private readonly StringBuilder _reply = new StringBuilder(2048);
        private LuStatus _lastStatus = LuStatus.Idle;
        private LuParams _params;

        // A decoder held across polls: even though the native side never splits
        // a UTF-8 sequence, holding one decoder avoids re-allocating per frame.
        private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
        private char[] _charBuffer;

        public string ModelPath => Path.Combine(Application.persistentDataPath, modelRelativePath);

        private void Awake()
        {
            _pollBuffer = new byte[Mathf.Max(256, maxBytesPerFrame) + 1];
            _charBuffer = new char[_pollBuffer.Length];
        }

        private IEnumerator Start()
        {
            // Give the first frame a chance to present before we start faulting
            // a multi-gigabyte mmap in; loading during the very first frame makes
            // the app look hung in-headset.
            yield return null;
            Load();
        }

        public void Load()
        {
            if (IsLoaded) return;

            string path = ModelPath;
            if (!File.Exists(path))
            {
                Fail($"No model at {path}\n" +
                     "Launch the app once, then push the GGUF with tools/push_model.sh.");
                return;
            }

            LlamaNative.SetLogToAndroid(logLlamaCppToLogcat ? 1 : 0);

            _params = new LuParams();
            LlamaNative.Defaults(ref _params);
            _params.NCtx              = contextTokens;
            _params.NThreads          = threads;
            _params.NGpuLayers        = 0;   // deliberate — see README
            _params.Temperature       = temperature;
            _params.TopP              = topP;
            _params.TopK              = topK;
            _params.RepeatPenalty     = repeatPenalty;
            _params.CpuMask           = cpuMask;
            _params.InterTokenDelayUs = 0;   // ThermalGovernor drives this

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ctx = LlamaNative.Load(path, ref _params);
            sw.Stop();

            if (_ctx == IntPtr.Zero)
            {
                Fail("Model load failed: " + LlamaNative.LastError(IntPtr.Zero));
                return;
            }

            Debug.Log($"[LlamaRunner] loaded in {sw.ElapsedMilliseconds} ms " +
                      $"(ctx={contextTokens}, threads={threads}, mask=0x{cpuMask:X})");

            if (!string.IsNullOrWhiteSpace(systemPrompt))
                LlamaNative.SetSystemPrompt(_ctx, systemPrompt);

            ContextSize = contextTokens;
            OnLoaded?.Invoke();
        }

        /// Queues a user turn. Returns false if a generation is already running.
        public bool Send(string userText)
        {
            if (!IsLoaded)
            {
                Fail("Send() before the model finished loading.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(userText)) return false;

            var r = LlamaNative.Submit(_ctx, userText, maxResponseTokens);
            if (r != LuResult.Ok)
            {
                if (r == LuResult.Busy) return false;
                Fail("Submit failed: " + LlamaNative.LastError(_ctx));
                return false;
            }

            _reply.Clear();
            _lastStatus = LuStatus.Running;
            return true;
        }

        public void Cancel()
        {
            if (IsLoaded) LlamaNative.Cancel(_ctx);
        }

        /// Clears the conversation and the KV cache. The system prompt survives.
        public void ResetConversation()
        {
            if (!IsLoaded) return;
            LlamaNative.Reset(_ctx);
            _reply.Clear();
            _lastStatus = LuStatus.Idle;
        }

        /// Live thermal knob, driven by ThermalGovernor. Takes effect on the
        /// next generated token — no reload, and safe mid-generation.
        public void SetInterTokenDelay(int microseconds)
        {
            microseconds = Mathf.Max(0, microseconds);
            _params.InterTokenDelayUs = microseconds;
            if (IsLoaded) LlamaNative.SetThrottle(_ctx, microseconds);
        }

        private void Update()
        {
            if (!IsLoaded) return;

            int status = LlamaNative.Poll(_ctx, _pollBuffer, _pollBuffer.Length, out int written);

            if (written > 0)
            {
                int chars = _utf8.GetChars(_pollBuffer, 0, written, _charBuffer, 0);
                if (chars > 0)
                {
                    string chunk = new string(_charBuffer, 0, chars);
                    _reply.Append(chunk);
                    OnToken?.Invoke(chunk);
                }
            }

            var s = (LuStatus) status;
            if (s == _lastStatus) return;

            // The worker can finish while output is still queued: lu_poll hands
            // back at most maxBytesPerFrame per call, so a fast burst outlives
            // the status change. Firing completion here would report a reply
            // missing its tail, and would order OnComplete before the OnToken
            // events that follow it. Hold the edge until the queue is empty.
            bool terminal = s == LuStatus.Done || s == LuStatus.Cancelled ||
                            s == LuStatus.Error;
            if (terminal && written > 0) return;

            // Edge-triggered: only fire completion once per job.
            _lastStatus = s;
            switch (s)
            {
                case LuStatus.Done:
                case LuStatus.Cancelled:
                    LlamaNative.Stats(_ctx, out float ptps, out float gtps,
                                      out int nPast, out int nCtx);
                    PromptTokensPerSecond = ptps;
                    GenTokensPerSecond    = gtps;
                    ContextUsed = nPast;
                    ContextSize = nCtx;
                    OnComplete?.Invoke(_reply.ToString());
                    break;

                case LuStatus.Error:
                    Fail(LlamaNative.LastError(_ctx));
                    break;
            }
        }

        private void Fail(string message)
        {
            Debug.LogError("[LlamaRunner] " + message);
            OnError?.Invoke(message);
        }

        private void OnDestroy()
        {
            if (_ctx == IntPtr.Zero) return;

            // lu_free cancels and joins the worker. Doing this on OnDestroy
            // rather than OnApplicationQuit matters: Quest suspends rather than
            // quits when the user takes the headset off, and a live worker
            // thread on a suspended app is what drains the battery in a pocket.
            IntPtr ctx = _ctx;
            _ctx = IntPtr.Zero;
            LlamaNative.Free(ctx);
        }

        private void OnApplicationPause(bool paused)
        {
            // Proximity sensor off -> app paused. Stop burning cores immediately.
            if (paused && IsGenerating) Cancel();
        }
    }
}
