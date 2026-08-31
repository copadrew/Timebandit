// VoiceInput.cs — push-to-talk and hands-free dictation, fully on-device.
//
// Pipeline: Unity Microphone (48 kHz mono ring buffer)
//        -> energy VAD with hangover
//        -> linear resample to 16 kHz
//        -> whisper.cpp on a worker thread
//        -> transcript handed to LlamaRunner.
//
// Two notes on why it looks like this:
//
//  * Unity's Microphone writes into a looping AudioClip; you poll GetPosition()
//    and read the delta. There is no callback. Reading the whole clip every
//    frame is the common mistake and it allocates megabytes per second.
//
//  * We do NOT stream partial audio into whisper. Whisper is an encoder-decoder
//    over a fixed 30 s window; feeding it 200 ms slices produces confident
//    nonsense. Capture a full utterance, then transcribe once.

using System;
using System.Text;
using QuestLlm.Native;
using UnityEngine;
using UnityEngine.Android;

namespace QuestLlm.Runtime
{
    [DisallowMultipleComponent]
    public sealed class VoiceInput : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("Path relative to Application.persistentDataPath. base.en quantised " +
                 "is about 60 MB and is the right size for this device; anything " +
                 "larger competes with the LLM for the same cores.")]
        public string whisperModelRelativePath = "models/ggml-base.en-q5_1.bin";

        [Header("Capture")]
        [Tooltip("Ring buffer length. Also the hard cap on one utterance.")]
        public int maxUtteranceSeconds = 15;
        public int microphoneSampleRate = 48000;

        [Header("Voice activity detection")]
        [Tooltip("RMS above this counts as speech. Quest's mic array is fairly " +
                 "clean; 0.012 is a reasonable starting point for a quiet room.")]
        public float speechThreshold = 0.012f;

        [Tooltip("Silence after speech before the utterance is considered over.")]
        public float trailingSilenceSeconds = 0.8f;

        [Tooltip("Ignore utterances shorter than this — usually a cough or a " +
                 "controller click.")]
        public float minUtteranceSeconds = 0.35f;

        [Header("Threads")]
        [Tooltip("Keep this mask disjoint from LlamaRunner.cpuMask. 0x0C is " +
                 "cores 2-3; the LLM default 0x70 is cores 4-6.")]
        public uint cpuMask = 0x0C;
        public int threads = 2;

        [Header("Behaviour")]
        [Tooltip("Hands-free: start listening automatically whenever the model " +
                 "is idle. Off means push-to-talk via BeginListening/EndListening.")]
        public bool continuousListening = false;

        public event Action<string> OnTranscript;
        public event Action<string> OnError;
        public event Action<bool>   OnListeningChanged;

        public bool IsListening    { get; private set; }
        public bool IsTranscribing { get; private set; }
        public float CurrentLevel  { get; private set; }

        private IntPtr _ctx = IntPtr.Zero;
        private string _device;
        private AudioClip _clip;
        private int _readPos;

        private float[] _frameBuffer;      // scratch for one read
        private float[] _utterance;        // accumulated speech at mic rate
        private int _utteranceLen;
        private float[] _resampled;        // 16 kHz copy handed to whisper
        private byte[] _transcriptBuffer = new byte[4096];

        private float _silenceTimer;
        private bool  _sawSpeech;
        private bool  _permissionRequested;

        private void Awake()
        {
            int maxSamples = maxUtteranceSeconds * microphoneSampleRate;
            _frameBuffer = new float[microphoneSampleRate / 4];   // 250 ms of headroom
            _utterance   = new float[maxSamples];
            _resampled   = new float[maxUtteranceSeconds * WhisperNative.SampleRate + 16];
        }

        private void Start()
        {
            RequestMicrophonePermission();
            LoadModel();
        }

        private void RequestMicrophonePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                _permissionRequested = true;
                Permission.RequestUserPermission(Permission.Microphone);
            }
#endif
        }

        private bool HasMicrophonePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
            return true;
#endif
        }

        private void LoadModel()
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath,
                                                 whisperModelRelativePath);
            if (!System.IO.File.Exists(path))
            {
                Fail($"No whisper model at {path}. Push it with tools/push_model.sh.");
                return;
            }

            var p = new WuParams();
            WhisperNative.Defaults(ref p);
            p.NThreads = threads;
            p.CpuMask  = cpuMask;
            p.Language = "en";

            _ctx = WhisperNative.Load(path, ref p);
            if (_ctx == IntPtr.Zero)
                Fail("Whisper load failed: " + WhisperNative.LastError(IntPtr.Zero));
            else
                Debug.Log($"[VoiceInput] whisper loaded (threads={threads}, mask=0x{cpuMask:X})");
        }

        public void BeginListening()
        {
            if (IsListening || _ctx == IntPtr.Zero) return;

            if (!HasMicrophonePermission())
            {
                if (!_permissionRequested) RequestMicrophonePermission();
                Fail("Microphone permission not granted yet.");
                return;
            }

            if (Microphone.devices.Length == 0)
            {
                Fail("No microphone device reported by Unity.");
                return;
            }

            _device = Microphone.devices[0];
            _clip = Microphone.Start(_device, true, maxUtteranceSeconds, microphoneSampleRate);
            if (_clip == null)
            {
                Fail("Microphone.Start returned null.");
                return;
            }

            _readPos = 0;
            _utteranceLen = 0;
            _silenceTimer = 0f;
            _sawSpeech = false;
            IsListening = true;
            OnListeningChanged?.Invoke(true);
        }

        /// Stops capture and, if enough speech was collected, transcribes it.
        public void EndListening()
        {
            if (!IsListening) return;

            DrainMicrophone();
            Microphone.End(_device);
            IsListening = false;
            OnListeningChanged?.Invoke(false);

            float seconds = _utteranceLen / (float) microphoneSampleRate;
            if (_sawSpeech && seconds >= minUtteranceSeconds)
                Transcribe();

            _utteranceLen = 0;
            _sawSpeech = false;

            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }
        }

        private void Update()
        {
            if (IsListening) { DrainMicrophone(); CheckEndOfUtterance(); }
            if (IsTranscribing) PollTranscript();

            if (continuousListening && !IsListening && !IsTranscribing && _ctx != IntPtr.Zero)
                BeginListening();
        }

        // Reads only the samples written since the last call, tracking the
        // microphone's write head around the ring buffer.
        private void DrainMicrophone()
        {
            if (_clip == null) return;

            int writePos = Microphone.GetPosition(_device);
            if (writePos < 0) return;

            int available = writePos - _readPos;
            if (available < 0) available += _clip.samples;   // wrapped
            if (available <= 0) return;

            while (available > 0)
            {
                int chunk = Mathf.Min(available, _frameBuffer.Length);
                chunk = Mathf.Min(chunk, _clip.samples - _readPos);
                if (chunk <= 0) break;

                _clip.GetData(_frameBuffer, _readPos);
                AppendSamples(_frameBuffer, chunk);

                _readPos = (_readPos + chunk) % _clip.samples;
                available -= chunk;
            }
        }

        private void AppendSamples(float[] src, int count)
        {
            double sumSq = 0.0;
            for (int i = 0; i < count; i++) sumSq += src[i] * (double) src[i];
            CurrentLevel = (float) Math.Sqrt(sumSq / Math.Max(1, count));

            bool speech = CurrentLevel >= speechThreshold;
            if (speech)
            {
                _sawSpeech = true;
                _silenceTimer = 0f;
            }

            // Before any speech we keep nothing, so leading silence does not eat
            // the buffer. Once speech starts we record everything, trailing
            // silence included — whisper uses that context.
            if (!_sawSpeech) return;

            int room = _utterance.Length - _utteranceLen;
            int n = Mathf.Min(count, room);
            if (n > 0)
            {
                Array.Copy(src, 0, _utterance, _utteranceLen, n);
                _utteranceLen += n;
            }
        }

        private void CheckEndOfUtterance()
        {
            if (_utteranceLen >= _utterance.Length)
            {
                EndListening();   // hit the hard cap
                return;
            }
            if (!_sawSpeech) return;

            if (CurrentLevel < speechThreshold)
            {
                _silenceTimer += Time.unscaledDeltaTime;
                if (_silenceTimer >= trailingSilenceSeconds) EndListening();
            }
        }

        // Linear interpolation from the mic rate to whisper's required 16 kHz.
        // Good enough for speech: whisper's own front end is a mel spectrogram
        // and is not sensitive to the resampler's stopband.
        private int ResampleTo16k(float[] src, int srcLen, int srcRate, float[] dst)
        {
            if (srcLen <= 0) return 0;
            if (srcRate == WhisperNative.SampleRate)
            {
                int n = Mathf.Min(srcLen, dst.Length);
                Array.Copy(src, dst, n);
                return n;
            }

            double ratio = WhisperNative.SampleRate / (double) srcRate;
            int outLen = Mathf.Min((int) (srcLen * ratio), dst.Length);

            for (int i = 0; i < outLen; i++)
            {
                double srcPos = i / ratio;
                int i0 = (int) srcPos;
                int i1 = Mathf.Min(i0 + 1, srcLen - 1);
                float frac = (float) (srcPos - i0);
                dst[i] = Mathf.Lerp(src[i0], src[i1], frac);
            }
            return outLen;
        }

        private void Transcribe()
        {
            int n = ResampleTo16k(_utterance, _utteranceLen, microphoneSampleRate, _resampled);
            if (n <= 0) return;

            int r = WhisperNative.Submit(_ctx, _resampled, n);
            if (r != 0)
            {
                Fail("Whisper submit failed: " + WhisperNative.LastError(_ctx));
                return;
            }
            IsTranscribing = true;
        }

        private void PollTranscript()
        {
            int status = WhisperNative.Poll(_ctx, _transcriptBuffer,
                                            _transcriptBuffer.Length, out int written);

            if (status == (int) WuStatus.Error)
            {
                IsTranscribing = false;
                Fail("Transcription failed: " + WhisperNative.LastError(_ctx));
                return;
            }
            if (status != (int) WuStatus.Done) return;

            IsTranscribing = false;
            if (written <= 0) return;

            string text = Encoding.UTF8.GetString(_transcriptBuffer, 0, written);
            if (!string.IsNullOrWhiteSpace(text)) OnTranscript?.Invoke(text);
        }

        private void Fail(string message)
        {
            Debug.LogError("[VoiceInput] " + message);
            OnError?.Invoke(message);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && IsListening) EndListening();
        }

        private void OnDestroy()
        {
            if (IsListening && !string.IsNullOrEmpty(_device)) Microphone.End(_device);
            if (_ctx != IntPtr.Zero)
            {
                IntPtr ctx = _ctx;
                _ctx = IntPtr.Zero;
                WhisperNative.Free(ctx);
            }
        }
    }
}
