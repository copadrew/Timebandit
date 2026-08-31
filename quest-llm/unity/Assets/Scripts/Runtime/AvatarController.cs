// AvatarController.cs — gives the model a face.
//
// Everything here is optional and driven by whatever transforms you wire up, so
// it works with a sphere and two smaller spheres for eyes before you have any
// real art, and upgrades to blendshapes on a rigged head without code changes.
//
// Design constraints worth stating, because they are what stop this from being
// set dressing:
//
//  * Nothing in here runs a model. Gaze, blink and mouth motion are procedural
//    and cost microseconds. The device's entire CPU budget is already spoken
//    for by llama.cpp and whisper.cpp.
//
//  * Lip sync is driven by the system TTS's speaking flag, not by phoneme
//    analysis. Real visemes would need the synthesised audio buffer, which the
//    Android TextToSpeech service does not hand back without writing to a file
//    and re-reading it. Procedural jaw motion at a plausible syllable rate is
//    indistinguishable at conversational distance and costs nothing.
//
//  * If TTS is unavailable the avatar still animates, driven by token arrival
//    instead. Losing audio should not leave a frozen face on the panel.

using System;
using System.Text;
using UnityEngine;

namespace QuestLlm.Runtime
{
    [DisallowMultipleComponent]
    public sealed class AvatarController : MonoBehaviour
    {
        public enum AvatarState { Idle, Listening, Thinking, Speaking }

        [Header("Wiring")]
        public LlamaRunner runner;
        public VoiceInput voice;      // optional
        public AndroidTts tts;        // optional

        [Header("Rig (all optional)")]
        [Tooltip("Rotated to face the player. Leave null to disable gaze.")]
        public Transform head;

        [Tooltip("Scaled on Y to open and close. Works with a flattened cube.")]
        public Transform mouth;

        public Transform leftEyelid;
        public Transform rightEyelid;

        [Tooltip("Whole-avatar root for idle sway. Defaults to this transform.")]
        public Transform body;

        [Header("Rig (blendshape path)")]
        [Tooltip("If set, blendshapes are driven instead of the transforms above.")]
        public SkinnedMeshRenderer faceRenderer;
        public int jawOpenBlendShapeIndex = -1;
        public int blinkBlendShapeIndex   = -1;

        [Header("State tint (optional)")]
        [Tooltip("Renderer tinted to signal state — the cheap way to make an " +
                 "untextured orb legible.")]
        public Renderer stateRenderer;
        public Color idleColor      = new Color(0.55f, 0.58f, 0.62f);
        public Color listeningColor = new Color(0.90f, 0.35f, 0.25f);
        public Color thinkingColor  = new Color(0.95f, 0.72f, 0.25f);
        public Color speakingColor  = new Color(0.35f, 0.70f, 0.95f);
        public float tintLerpSpeed  = 6f;

        [Header("Gaze")]
        public Transform lookTarget;         // defaults to the main camera
        [Range(0f, 20f)] public float gazeSpeed = 6f;
        [Tooltip("Maximum head turn from rest, degrees. Keeps it from snapping " +
                 "round to stare at someone standing behind it.")]
        public float maxYaw = 55f;
        public float maxPitch = 30f;

        [Header("Mouth")]
        [Tooltip("Approximate syllables per second while speaking.")]
        public float syllableRate = 5.5f;
        public float mouthOpenAmount = 0.75f;
        public float mouthCloseSpeed = 14f;

        [Header("Blink")]
        public Vector2 blinkIntervalRange = new Vector2(2.5f, 6f);
        public float blinkDuration = 0.09f;

        [Header("Idle motion")]
        public float swayAmplitude = 0.012f;
        public float swayFrequency = 0.35f;

        [Header("Speech")]
        [Tooltip("Speak each sentence as it completes rather than waiting for " +
                 "the full reply. At single-digit tokens/sec, waiting for the " +
                 "whole answer means seconds of silence staring at a still face.")]
        public bool speakSentencesAsTheyArrive = true;

        public AvatarState State { get; private set; } = AvatarState.Idle;

        private Quaternion _restRotation;
        private Vector3 _restPosition;
        private Vector3 _mouthRestScale = Vector3.one;
        private float _mouthOpen;
        private float _nextBlink;
        private float _blinkUntil;
        private float _tokenPulse;          // decays; proxies speech when TTS is off
        private MaterialPropertyBlock _mpb;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId     = Shader.PropertyToID("_Color");

        private readonly StringBuilder _pendingSpeech = new StringBuilder(256);

        private void Reset()
        {
            runner = GetComponentInParent<LlamaRunner>();
            voice  = GetComponentInParent<VoiceInput>();
            tts    = GetComponentInParent<AndroidTts>();
        }

        private void Awake()
        {
            if (body == null) body = transform;
            if (head != null) _restRotation = head.localRotation;
            _restPosition = body.localPosition;
            if (mouth != null) _mouthRestScale = mouth.localScale;
            if (lookTarget == null && Camera.main != null) lookTarget = Camera.main.transform;
            _mpb = new MaterialPropertyBlock();
            ScheduleBlink();
        }

        private void OnEnable()
        {
            if (runner != null)
            {
                runner.OnToken    += HandleToken;
                runner.OnComplete += HandleComplete;
            }
            if (tts != null) tts.OnSpeakingChanged += HandleSpeakingChanged;
        }

        private void OnDisable()
        {
            if (runner != null)
            {
                runner.OnToken    -= HandleToken;
                runner.OnComplete -= HandleComplete;
            }
            if (tts != null) tts.OnSpeakingChanged -= HandleSpeakingChanged;
        }

        // --- stream handling -------------------------------------------------

        private void HandleToken(string chunk)
        {
            // Any token arriving is evidence of life, used for mouth motion when
            // there is no TTS to sync to.
            _tokenPulse = 1f;

            if (tts == null || !tts.IsAvailable || !speakSentencesAsTheyArrive) return;

            _pendingSpeech.Append(chunk);
            FlushCompleteSentences();
        }

        private void HandleComplete(string fullReply)
        {
            if (tts == null || !tts.IsAvailable) return;

            if (speakSentencesAsTheyArrive)
            {
                // Whatever is left has no terminator — a trailing clause, or a
                // reply cut short by the token budget. Speak it anyway.
                string tail = _pendingSpeech.ToString().Trim();
                _pendingSpeech.Clear();
                if (tail.Length > 0) tts.Enqueue(tail);
            }
            else
            {
                tts.SpeakNow(fullReply);
            }
        }

        // Emits on sentence terminators. Also flushes on length, so a model that
        // produces a wall of text with no punctuation still speaks.
        private void FlushCompleteSentences()
        {
            const int hardFlushAt = 180;

            int lastBreak = -1;
            for (int i = 0; i < _pendingSpeech.Length; i++)
            {
                char c = _pendingSpeech[i];
                if (c == '.' || c == '!' || c == '?' || c == '\n') lastBreak = i;
            }

            if (lastBreak < 0)
            {
                if (_pendingSpeech.Length < hardFlushAt) return;
                lastBreak = _pendingSpeech.Length - 1;
            }

            string sentence = _pendingSpeech.ToString(0, lastBreak + 1).Trim();
            _pendingSpeech.Remove(0, lastBreak + 1);
            if (sentence.Length > 0) tts.Enqueue(sentence);
        }

        private void HandleSpeakingChanged(bool speaking)
        {
            if (!speaking) _mouthOpen = 0f;
        }

        // --- animation -------------------------------------------------------

        private void Update()
        {
            float dt = Time.deltaTime;
            _tokenPulse = Mathf.Max(0f, _tokenPulse - dt * 2.5f);

            State = ResolveState();

            DriveGaze(dt);
            DriveMouth(dt);
            DriveBlink();
            DriveSway();
            DriveTint(dt);
        }

        private AvatarState ResolveState()
        {
            if (tts != null && tts.IsSpeaking) return AvatarState.Speaking;
            if (voice != null && voice.IsListening) return AvatarState.Listening;

            if (runner != null && runner.IsGenerating)
            {
                bool noAudio = tts == null || !tts.IsAvailable;
                // Without TTS, streaming tokens are the closest thing we have to
                // "talking", so the face should move rather than sit in Thinking.
                return (noAudio && _tokenPulse > 0f) ? AvatarState.Speaking
                                                     : AvatarState.Thinking;
            }

            if (voice != null && voice.IsTranscribing) return AvatarState.Thinking;
            return AvatarState.Idle;
        }

        private void DriveGaze(float dt)
        {
            if (head == null || lookTarget == null) return;

            Vector3 toTarget = lookTarget.position - head.position;
            if (toTarget.sqrMagnitude < 1e-6f) return;

            Quaternion want = Quaternion.LookRotation(toTarget, Vector3.up);

            // Clamp in the head's parent space so the limits mean "relative to
            // rest pose", not "relative to world forward".
            Quaternion local = Quaternion.Inverse(head.parent != null
                                                  ? head.parent.rotation
                                                  : Quaternion.identity) * want;

            Vector3 e = (Quaternion.Inverse(_restRotation) * local).eulerAngles;
            float yaw   = Mathf.Clamp(Mathf.DeltaAngle(0f, e.y), -maxYaw,   maxYaw);
            float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), -maxPitch, maxPitch);

            Quaternion clamped = _restRotation * Quaternion.Euler(pitch, yaw, 0f);
            head.localRotation = Quaternion.Slerp(head.localRotation, clamped,
                                                  1f - Mathf.Exp(-gazeSpeed * dt));
        }

        private void DriveMouth(float dt)
        {
            bool talking = State == AvatarState.Speaking;

            if (talking)
            {
                // Two offset noise bands: a syllable-rate envelope and a faster
                // jitter, so it does not read as a metronome.
                float t = Time.time * syllableRate;
                float envelope = Mathf.PerlinNoise(t, 0.37f);
                float jitter   = Mathf.PerlinNoise(t * 2.7f, 11.3f) * 0.35f;
                float target = Mathf.Clamp01((envelope + jitter) * mouthOpenAmount);
                _mouthOpen = Mathf.Lerp(_mouthOpen, target, 1f - Mathf.Exp(-18f * dt));
            }
            else
            {
                float rest = State == AvatarState.Thinking ? 0.06f : 0f;
                _mouthOpen = Mathf.Lerp(_mouthOpen, rest,
                                        1f - Mathf.Exp(-mouthCloseSpeed * dt));
            }

            if (faceRenderer != null && jawOpenBlendShapeIndex >= 0)
            {
                faceRenderer.SetBlendShapeWeight(jawOpenBlendShapeIndex, _mouthOpen * 100f);
            }
            else if (mouth != null)
            {
                var s = _mouthRestScale;
                s.y = _mouthRestScale.y * (1f + _mouthOpen * 3.5f);
                mouth.localScale = s;
            }
        }

        private void DriveBlink()
        {
            float now = Time.time;
            if (now >= _nextBlink)
            {
                _blinkUntil = now + blinkDuration;
                ScheduleBlink();
            }

            float closed = now < _blinkUntil ? 1f : 0f;

            if (faceRenderer != null && blinkBlendShapeIndex >= 0)
            {
                faceRenderer.SetBlendShapeWeight(blinkBlendShapeIndex, closed * 100f);
                return;
            }

            // Transform path: squash the eyelids flat on the Y axis.
            ApplyLid(leftEyelid,  closed);
            ApplyLid(rightEyelid, closed);
        }

        private static void ApplyLid(Transform lid, float closed)
        {
            if (lid == null) return;
            var s = lid.localScale;
            s.y = Mathf.Lerp(1f, 0.05f, closed);
            lid.localScale = s;
        }

        private void ScheduleBlink()
        {
            float lo = Mathf.Min(blinkIntervalRange.x, blinkIntervalRange.y);
            float hi = Mathf.Max(blinkIntervalRange.x, blinkIntervalRange.y);
            _nextBlink = Time.time + UnityEngine.Random.Range(lo, Mathf.Max(lo + 0.1f, hi));
        }

        private void DriveSway()
        {
            if (body == null || swayAmplitude <= 0f) return;
            float t = Time.time * swayFrequency;
            var offset = new Vector3(Mathf.Sin(t * 1.3f), Mathf.Sin(t), 0f) * swayAmplitude;
            body.localPosition = _restPosition + offset;
        }

        private void DriveTint(float dt)
        {
            if (stateRenderer == null) return;

            Color target = State switch
            {
                AvatarState.Listening => listeningColor,
                AvatarState.Thinking  => thinkingColor,
                AvatarState.Speaking  => speakingColor,
                _                     => idleColor,
            };

            // MaterialPropertyBlock rather than .material: touching .material
            // instantiates a copy of the material every call, which on Quest is
            // both a per-frame allocation and an extra draw-call batch break.
            stateRenderer.GetPropertyBlock(_mpb);
            Color current = _mpb.HasColor(BaseColorId) ? _mpb.GetColor(BaseColorId)
                          : _mpb.HasColor(ColorId)     ? _mpb.GetColor(ColorId)
                          : target;
            Color next = Color.Lerp(current, target, 1f - Mathf.Exp(-tintLerpSpeed * dt));

            // URP uses _BaseColor, the built-in pipeline uses _Color. Setting
            // both is cheaper than branching on the active render pipeline.
            _mpb.SetColor(BaseColorId, next);
            _mpb.SetColor(ColorId, next);
            stateRenderer.SetPropertyBlock(_mpb);
        }
    }
}
