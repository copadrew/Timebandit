// AndroidTts.cs — speech output via the system TextToSpeech service.
//
// Why the system engine and not Piper/Kokoro/anything GGUF:
//
// A neural TTS model would be a third native library and a third consumer of a
// CPU budget that is already oversubscribed by llama.cpp and whisper.cpp on a
// passively-cooled headset. android.speech.tts.TextToSpeech is an existing
// system service — it runs in its own process, on the system's schedule, and
// costs this app no worker threads at all. The voice is worse. The frame rate
// is better. On this device that is the correct trade.
//
// Degrading gracefully matters here: a Quest may ship without voice data for
// the requested locale, in which case IsAvailable stays false and the avatar
// falls back to token-cadence mouth motion with no audio. That is a normal
// state, not an error.

using System;
using System.Collections;
using UnityEngine;

namespace QuestLlm.Runtime
{
    [DisallowMultipleComponent]
    public sealed class AndroidTts : MonoBehaviour
    {
        [Tooltip("Speech rate multiplier. 1.0 is the engine default; slightly " +
                 "slower reads better through a headset's speakers.")]
        [Range(0.5f, 2f)] public float speechRate = 0.95f;

        [Range(0.5f, 2f)] public float pitch = 1.0f;

        [Tooltip("BCP-47 tag used to pick the voice.")]
        public string language = "en-US";

        public bool IsAvailable { get; private set; }
        public bool IsSpeaking  { get; private set; }

        public event Action<bool> OnSpeakingChanged;

        private AndroidJavaObject _tts;
        private bool _initialised;

        // TextToSpeech constants (android.speech.tts.TextToSpeech).
        private const int QUEUE_FLUSH = 0;
        private const int QUEUE_ADD   = 1;

        private IEnumerator Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Create()) yield break;

            // The engine initialises asynchronously. There is no callback we can
            // subclass from C# — UtteranceProgressListener is an abstract class
            // and AndroidJavaProxy only implements interfaces — so we probe
            // until a call that requires an initialised engine stops throwing.
            float deadline = Time.unscaledTime + 5f;
            while (Time.unscaledTime < deadline && !_initialised)
            {
                if (Probe()) break;
                yield return new WaitForSeconds(0.25f);
            }

            if (!_initialised)
                Debug.LogWarning("[AndroidTts] engine did not initialise; " +
                                 "continuing without speech output.");
#else
            yield break;
#endif
        }

        private bool Create()
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    // The two-arg constructor takes an OnInitListener; passing
                    // null is legal and we poll instead.
                    _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech",
                                                 activity, null);
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AndroidTts] unavailable: " + e.Message);
                _tts = null;
                return false;
            }
        }

        private bool Probe()
        {
            if (_tts == null) return false;
            try
            {
                using (var locale = new AndroidJavaObject("java.util.Locale", language))
                {
                    // LANG_MISSING_DATA (-1) and LANG_NOT_SUPPORTED (-2) mean the
                    // engine is up but cannot speak this language.
                    int result = _tts.Call<int>("setLanguage", locale);
                    if (result < 0)
                    {
                        Debug.LogWarning($"[AndroidTts] no voice data for {language} " +
                                         $"(setLanguage returned {result}).");
                        _initialised = true;   // stop probing; IsAvailable stays false
                        return true;
                    }
                }

                _tts.Call<int>("setSpeechRate", speechRate);
                _tts.Call<int>("setPitch", pitch);

                _initialised = true;
                IsAvailable  = true;
                Debug.Log("[AndroidTts] ready.");
                return true;
            }
            catch (Exception)
            {
                return false;   // engine still binding
            }
        }

        /// Queues text behind anything already speaking. Used for sentence-at-a-
        /// time playback so the avatar starts talking before generation ends.
        public void Enqueue(string text) => Speak(text, QUEUE_ADD);

        /// Interrupts whatever is playing and speaks this instead.
        public void SpeakNow(string text) => Speak(text, QUEUE_FLUSH);

        private void Speak(string text, int queueMode)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                // speak(CharSequence, int, Bundle, String) is the API 21+ form.
                _tts.Call<int>("speak", text, queueMode, null, "questllm");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AndroidTts] speak failed: " + e.Message);
            }
        }

        public void Stop()
        {
            if (!IsAvailable) return;
            try { _tts.Call<int>("stop"); } catch (Exception) { /* engine going away */ }
        }

        private void Update()
        {
            if (!IsAvailable) return;

            bool speaking;
            try { speaking = _tts.Call<bool>("isSpeaking"); }
            catch (Exception) { return; }

            if (speaking == IsSpeaking) return;
            IsSpeaking = speaking;
            OnSpeakingChanged?.Invoke(speaking);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) Stop();
        }

        private void OnDestroy()
        {
            if (_tts == null) return;
            try
            {
                _tts.Call<int>("stop");
                _tts.Call("shutdown");   // releases the engine binding
            }
            catch (Exception) { /* nothing useful to do while tearing down */ }
            _tts.Dispose();
            _tts = null;
        }
    }
}
