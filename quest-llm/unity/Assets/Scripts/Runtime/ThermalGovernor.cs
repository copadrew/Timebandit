// ThermalGovernor.cs
//
// This component is the answer to the objection that sinks most "LLM in a
// headset" projects: a Quest 3 is a passively-cooled SoC strapped to a face.
// Saturating three cores with int4 matmuls is a sustained all-core load of
// exactly the kind the thermal design does not budget for. Left alone, the
// device does not politely slow your inference — it throttles the whole SoC,
// including the GPU and the compositor, and the user gets judder, which in VR
// means nausea.
//
// So we throttle ourselves first, on two independent signals:
//
//   1. Android's own thermal status (PowerManager.getCurrentThermalStatus,
//      API 29+). This is the device telling us it is already in trouble.
//   2. Measured frame time. This catches the case where we are starving the
//      render thread for CPU before the SoC is hot enough to complain.
//
// Signal 2 usually fires first, which is the point: by the time signal 1 trips
// you have already shipped a bad experience.

using System.Collections;
using UnityEngine;

namespace QuestLlm.Runtime
{
    [RequireComponent(typeof(LlamaRunner))]
    public sealed class ThermalGovernor : MonoBehaviour
    {
        public enum ThermalStatus
        {
            None = 0, Light = 1, Moderate = 2, Severe = 3,
            Critical = 4, Emergency = 5, Shutdown = 6, Unknown = -1,
        }

        [Header("Frame-time guard")]
        [Tooltip("Target refresh rate. Quest 3 supports 72/80/90/120 Hz; whatever " +
                 "you set in OVRManager should match this.")]
        public float targetHz = 72f;

        [Tooltip("Fraction of the frame budget we tolerate before backing off. " +
                 "1.15 means 'allow 15% over budget before throttling'.")]
        public float frameBudgetSlack = 1.15f;

        [Tooltip("Frames averaged before acting. Too low and you oscillate on a " +
                 "single hitch; too high and you react after the user notices.")]
        public int sampleWindow = 60;

        [Header("Throttle range (microseconds of sleep per token)")]
        public int minDelayUs = 0;
        public int maxDelayUs = 40000;   // 40 ms/token ~= 25 tok/s ceiling
        public int stepUpUs   = 4000;
        public int stepDownUs = 1000;

        [Header("Hard stops")]
        [Tooltip("At or above this thermal status, cancel generation outright. " +
                 "Severe means the device is already throttling the GPU.")]
        public ThermalStatus cancelAtOrAbove = ThermalStatus.Severe;

        [Header("Diagnostics")]
        public bool verbose = false;

        public ThermalStatus CurrentStatus { get; private set; } = ThermalStatus.Unknown;
        public int CurrentDelayUs { get; private set; }
        public float SmoothedFrameMs { get; private set; }

        private LlamaRunner _runner;
        private AndroidJavaObject _powerManager;
        private float _accum;
        private int _samples;

        private void Awake()
        {
            _runner = GetComponent<LlamaRunner>();
            SmoothedFrameMs = 1000f / Mathf.Max(1f, targetHz);
        }

        private IEnumerator Start()
        {
            AcquirePowerManager();
            // Thermal state moves on the order of tens of seconds; polling it
            // through JNI every frame would cost more than it saves.
            while (true)
            {
                PollThermalStatus();
                yield return new WaitForSeconds(2f);
            }
        }

        private void AcquirePowerManager()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    _powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ThermalGovernor] no PowerManager; falling back to " +
                                 "frame-time signal only. " + e.Message);
                _powerManager = null;
            }
#else
            _powerManager = null;
#endif
        }

        private void PollThermalStatus()
        {
            if (_powerManager == null) return;
            try
            {
                // getCurrentThermalStatus() is API 29+. Quest 3 is API 32, so
                // this is safe on the only device we target.
                int raw = _powerManager.Call<int>("getCurrentThermalStatus");
                var status = (ThermalStatus) raw;
                if (status != CurrentStatus && verbose)
                    Debug.Log($"[ThermalGovernor] thermal status -> {status}");
                CurrentStatus = status;
            }
            catch (System.Exception)
            {
                CurrentStatus = ThermalStatus.Unknown;
            }
        }

        private void Update()
        {
            if (!_runner.IsLoaded) return;

            // Rolling mean of frame time. unscaledDeltaTime so a paused or
            // slow-motion timescale does not read as thermal distress.
            _accum += Time.unscaledDeltaTime * 1000f;
            _samples++;
            if (_samples < Mathf.Max(1, sampleWindow)) return;

            SmoothedFrameMs = _accum / _samples;
            _accum = 0f;
            _samples = 0;

            if (!_runner.IsGenerating)
            {
                // Idle: walk the throttle back down so the next reply starts fast.
                Adjust(-stepDownUs);
                return;
            }

            if (CurrentStatus >= cancelAtOrAbove && CurrentStatus != ThermalStatus.Unknown)
            {
                Debug.LogWarning($"[ThermalGovernor] {CurrentStatus}: cancelling generation.");
                _runner.Cancel();
                Adjust(maxDelayUs);
                return;
            }

            float budgetMs = 1000f / Mathf.Max(1f, targetHz);
            float ceilingMs = budgetMs * Mathf.Max(1f, frameBudgetSlack);

            bool hot = CurrentStatus >= ThermalStatus.Moderate &&
                       CurrentStatus != ThermalStatus.Unknown;

            if (SmoothedFrameMs > ceilingMs || hot)
            {
                Adjust(stepUpUs);
                if (verbose)
                    Debug.Log($"[ThermalGovernor] frame {SmoothedFrameMs:F1} ms > " +
                              $"{ceilingMs:F1} ms (thermal {CurrentStatus}) " +
                              $"-> delay {CurrentDelayUs} us");
            }
            else if (SmoothedFrameMs < budgetMs * 0.9f)
            {
                Adjust(-stepDownUs);
            }
        }

        private void Adjust(int deltaUs)
        {
            int next = Mathf.Clamp(CurrentDelayUs + deltaUs, minDelayUs, maxDelayUs);
            if (next == CurrentDelayUs) return;
            CurrentDelayUs = next;
            _runner.SetInterTokenDelay(next);
        }

        private void OnDestroy()
        {
            _powerManager?.Dispose();
            _powerManager = null;
        }
    }
}
