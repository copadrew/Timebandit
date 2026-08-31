// ChatPanel.cs — the in-headset UI: a world-space panel with streamed output,
// a mic button, and a keyboard fallback.
//
// It also owns the one piece of coordination the two native libraries cannot do
// for themselves: whisper and llama.cpp are pinned to disjoint core sets, but
// they still share memory bandwidth and a thermal envelope. Running both at
// once on this device costs more than it buys, so transcription and generation
// are serialised here — the mic is disabled while a reply streams.

using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestLlm.Runtime
{
    [DisallowMultipleComponent]
    public sealed class ChatPanel : MonoBehaviour
    {
        [Header("Wiring")]
        public LlamaRunner runner;
        public VoiceInput voice;                 // optional; leave null for text only

        [Header("UI")]
        public TMP_Text transcriptText;          // the conversation
        public TMP_Text statusText;              // model state, tok/s, thermal
        public TMP_InputField promptField;       // keyboard fallback
        public Button sendButton;
        public Button micButton;
        public Button cancelButton;
        public Button resetButton;
        public Image micLevelFill;               // optional level meter (fillAmount)

        [Header("Display")]
        [Tooltip("Conversation characters kept in the visible buffer. TMP re-lays " +
                 "out the whole string on every change; letting this grow without " +
                 "bound turns each streamed token into a progressively more " +
                 "expensive main-thread operation.")]
        public int maxVisibleCharacters = 4000;

        [Tooltip("Rebuilds of the transcript label per second while streaming. " +
                 "Tokens arrive faster than anyone reads; 20 Hz looks identical " +
                 "to per-token and costs a fraction of the layout work.")]
        public float refreshHz = 20f;

        private readonly StringBuilder _conversation = new StringBuilder(8192);
        private bool _dirty;
        private float _nextRefresh;
        private bool _pushToTalkHeld;
        private PointerHoldRelay _micHold;

        private void Reset()
        {
            runner = GetComponent<LlamaRunner>();
            voice  = GetComponent<VoiceInput>();
        }

        private void Awake()
        {
            if (runner == null) runner = GetComponent<LlamaRunner>();
            if (voice  == null) voice  = GetComponent<VoiceInput>();
        }

        private void OnEnable()
        {
            if (runner != null)
            {
                runner.OnLoaded   += HandleLoaded;
                runner.OnToken    += HandleToken;
                runner.OnComplete += HandleComplete;
                runner.OnError    += HandleError;
            }
            if (voice != null)
            {
                voice.OnTranscript       += HandleTranscript;
                voice.OnError            += HandleError;
                voice.OnListeningChanged += HandleListeningChanged;
            }

            if (sendButton   != null) sendButton.onClick.AddListener(SendFromField);
            if (cancelButton != null) cancelButton.onClick.AddListener(OnCancel);
            if (resetButton  != null) resetButton.onClick.AddListener(OnReset);
            if (promptField  != null) promptField.onSubmit.AddListener(_ => SendFromField());

            // The mic is push-to-talk: press starts capture, release ends it and
            // transcribes. A single toggle button in VR is worse — people forget
            // it is on and dictate their entire conversation into the prompt.
            if (micButton != null)
            {
                _micHold = micButton.GetComponent<PointerHoldRelay>();
                if (_micHold == null)
                    _micHold = micButton.gameObject.AddComponent<PointerHoldRelay>();
                _micHold.OnPressed  += BeginTalk;
                _micHold.OnReleased += EndTalk;
            }
        }

        private void OnDisable()
        {
            if (runner != null)
            {
                runner.OnLoaded   -= HandleLoaded;
                runner.OnToken    -= HandleToken;
                runner.OnComplete -= HandleComplete;
                runner.OnError    -= HandleError;
            }
            if (voice != null)
            {
                voice.OnTranscript       -= HandleTranscript;
                voice.OnError            -= HandleError;
                voice.OnListeningChanged -= HandleListeningChanged;
            }

            if (sendButton   != null) sendButton.onClick.RemoveListener(SendFromField);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(OnCancel);
            if (resetButton  != null) resetButton.onClick.RemoveListener(OnReset);

            if (_micHold != null)
            {
                _micHold.OnPressed  -= BeginTalk;
                _micHold.OnReleased -= EndTalk;
            }
        }

        private void Start()
        {
            AppendLine("<color=#8a8a8a>Loading model…</color>");
        }

        private void Update()
        {
            if (_dirty && Time.unscaledTime >= _nextRefresh)
            {
                Flush();
                _nextRefresh = Time.unscaledTime + 1f / Mathf.Max(1f, refreshHz);
            }

            UpdateStatusLine();
            UpdateInteractability();

            if (micLevelFill != null && voice != null)
                micLevelFill.fillAmount = Mathf.Clamp01(voice.CurrentLevel * 12f);
        }

        // --- input ----------------------------------------------------------

        public void SendFromField()
        {
            if (promptField == null) return;
            string text = promptField.text;
            if (string.IsNullOrWhiteSpace(text)) return;
            promptField.text = string.Empty;
            Submit(text);
        }

        private void BeginTalk()
        {
            if (voice == null || runner == null) return;
            if (runner.IsGenerating) return;   // serialised on purpose
            _pushToTalkHeld = true;
            voice.BeginListening();
        }

        private void EndTalk()
        {
            if (voice == null || !_pushToTalkHeld) return;
            _pushToTalkHeld = false;
            voice.EndListening();
        }

        private void Submit(string text)
        {
            if (runner == null || !runner.IsLoaded) return;
            AppendLine($"\n<b>You</b>\n{Escape(text)}\n");
            AppendLine("<b>Model</b>\n");
            if (!runner.Send(text))
                AppendLine("<color=#c05050>(busy — still answering the last one)</color>\n");
        }

        private void OnCancel() => runner?.Cancel();

        private void OnReset()
        {
            runner?.ResetConversation();
            _conversation.Clear();
            _dirty = true;
        }

        // --- model events ---------------------------------------------------

        private void HandleLoaded()
        {
            _conversation.Clear();
            AppendLine("<color=#8a8a8a>Model ready. Hold the mic button to talk.</color>\n");
        }

        private void HandleToken(string chunk)
        {
            _conversation.Append(Escape(chunk));
            _dirty = true;
        }

        private void HandleComplete(string _)
        {
            _conversation.Append('\n');
            _dirty = true;
            Flush();
        }

        private void HandleError(string message)
        {
            AppendLine($"\n<color=#c05050>{Escape(message)}</color>\n");
            Flush();
        }

        private void HandleTranscript(string text) => Submit(text);

        private void HandleListeningChanged(bool listening)
        {
            if (micButton == null) return;
            var colors = micButton.colors;
            colors.normalColor = listening ? new Color(0.85f, 0.25f, 0.15f)
                                           : Color.white;
            micButton.colors = colors;
        }

        // --- rendering ------------------------------------------------------

        private void AppendLine(string s)
        {
            _conversation.Append(s);
            _dirty = true;
        }

        private void Flush()
        {
            if (transcriptText == null) { _dirty = false; return; }

            if (_conversation.Length > maxVisibleCharacters)
            {
                int excess = _conversation.Length - maxVisibleCharacters;
                _conversation.Remove(0, excess);
            }
            transcriptText.SetText(_conversation);
            _dirty = false;
        }

        private void UpdateStatusLine()
        {
            if (statusText == null || runner == null) return;

            if (!runner.IsLoaded)
            {
                statusText.SetText("loading…");
                return;
            }

            string state = runner.IsGenerating ? "generating"
                         : (voice != null && voice.IsTranscribing) ? "transcribing"
                         : (voice != null && voice.IsListening) ? "listening"
                         : "idle";

            statusText.SetText(
                $"{state}   {runner.GenTokensPerSecond:F1} tok/s   " +
                $"ctx {runner.ContextUsed}/{runner.ContextSize}   " +
                $"{1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):F0} fps");
        }

        private void UpdateInteractability()
        {
            bool ready = runner != null && runner.IsLoaded;
            bool busy  = ready && runner.IsGenerating;

            if (sendButton   != null) sendButton.interactable   = ready && !busy;
            if (cancelButton != null) cancelButton.interactable  = busy;
            if (resetButton  != null) resetButton.interactable   = ready && !busy;
            if (micButton    != null)
                micButton.interactable = ready && !busy && voice != null && !voice.IsTranscribing;
        }

        // Model output is arbitrary text going into a TMP label that interprets
        // rich-text tags, and an uncensored model is exactly the kind that will
        // emit a stray '<' in code or markup. TMP has no entity escape, so the
        // standard workaround is a zero-width space before the bracket: the tag
        // parser stops matching, and the glyph renders with no visible gap.
        private const string ZeroWidthSpace = "\u200B";

        private static string Escape(string s)
            => string.IsNullOrEmpty(s) ? s : s.Replace("<", ZeroWidthSpace + "<");
    }
}
