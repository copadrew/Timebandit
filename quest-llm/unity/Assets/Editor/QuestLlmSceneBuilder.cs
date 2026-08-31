// QuestLlmSceneBuilder.cs — Tools ▸ Quest LLM ▸ Build Scene Rig
//
// Constructs the whole runtime rig from primitives and wires every reference,
// so the project runs in-headset before any art exists. Everything it makes is
// ordinary GameObjects you can then replace piecemeal: swap the orb for a
// rigged head, point AvatarController at its blendshapes, delete this script.
//
// Requires TextMeshPro essentials (Window ▸ TextMeshPro ▸ Import TMP Essential
// Resources) — the builder checks and tells you rather than throwing.

#if UNITY_EDITOR
using QuestLlm.Runtime;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestLlm.EditorTools
{
    public static class QuestLlmSceneBuilder
    {
        private const float PanelWidth  = 1200f;
        private const float PanelHeight = 800f;
        private const float PanelScale  = 0.0012f;   // world metres per UI unit

        [MenuItem("Tools/Quest LLM/Build Scene Rig")]
        public static void Build()
        {
            if (TMP_Settings.instance == null)
            {
                EditorUtility.DisplayDialog(
                    "TextMeshPro missing",
                    "Import TMP essentials first:\n\n" +
                    "Window ▸ TextMeshPro ▸ Import TMP Essential Resources",
                    "OK");
                return;
            }

            var root = new GameObject("QuestLLM");
            Undo.RegisterCreatedObjectUndo(root, "Build Quest LLM Rig");

            var runner  = root.AddComponent<LlamaRunner>();
            var voice   = root.AddComponent<VoiceInput>();
            var tts     = root.AddComponent<AndroidTts>();
            var thermal = root.AddComponent<ThermalGovernor>();
            var panel   = root.AddComponent<ChatPanel>();

            EnsureEventSystem();

            var avatar = BuildAvatar(root.transform, runner, voice, tts);
            BuildPanel(root.transform, panel, runner, voice);

            panel.runner = runner;
            panel.voice  = voice;

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            Debug.Log("[QuestLlmSceneBuilder] Rig built.\n" +
                      "Next: set Player Settings to IL2CPP + ARM64, target API 32+, " +
                      "then push a GGUF with tools/push_model.sh.");
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindObjectOfType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem",
                                    typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
            Debug.LogWarning("[QuestLlmSceneBuilder] Added a StandaloneInputModule so the " +
                             "UI is clickable in the Editor. In-headset you want a ray " +
                             "interactor from XRI or Meta's Interaction SDK driving the " +
                             "EventSystem instead — PointerHoldRelay works with either.");
        }

        // --- avatar ---------------------------------------------------------

        private static AvatarController BuildAvatar(Transform parent, LlamaRunner runner,
                                                    VoiceInput voice, AndroidTts tts)
        {
            var avatarRoot = new GameObject("Avatar");
            avatarRoot.transform.SetParent(parent, false);
            avatarRoot.transform.localPosition = new Vector3(-0.75f, 1.35f, 1.6f);

            var headGo = CreatePrimitive(PrimitiveType.Sphere, "Head", avatarRoot.transform,
                                         Vector3.zero, Vector3.one * 0.28f);

            // Eyes and mouth are children of the head so gaze rotation carries
            // them along without any extra bookkeeping.
            var leftEye  = CreatePrimitive(PrimitiveType.Sphere, "EyeL", headGo.transform,
                                           new Vector3(-0.22f, 0.12f, 0.44f),
                                           new Vector3(0.16f, 0.16f, 0.08f));
            var rightEye = CreatePrimitive(PrimitiveType.Sphere, "EyeR", headGo.transform,
                                           new Vector3(0.22f, 0.12f, 0.44f),
                                           new Vector3(0.16f, 0.16f, 0.08f));
            var mouth    = CreatePrimitive(PrimitiveType.Cube, "Mouth", headGo.transform,
                                           new Vector3(0f, -0.22f, 0.44f),
                                           new Vector3(0.30f, 0.04f, 0.06f));

            Tint(leftEye,  Color.black);
            Tint(rightEye, Color.black);
            Tint(mouth,    Color.black);

            var avatar = avatarRoot.AddComponent<AvatarController>();
            avatar.runner = runner;
            avatar.voice  = voice;
            avatar.tts    = tts;
            avatar.body   = avatarRoot.transform;
            avatar.head   = headGo.transform;
            avatar.mouth  = mouth.transform;
            avatar.leftEyelid  = leftEye.transform;
            avatar.rightEyelid = rightEye.transform;
            avatar.stateRenderer = headGo.GetComponent<Renderer>();

            return avatar;
        }

        private static GameObject CreatePrimitive(PrimitiveType type, string name,
                                                  Transform parent, Vector3 localPos,
                                                  Vector3 localScale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;

            // Colliders on decorative geometry only get in the way of UI rays.
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            return go;
        }

        private static void Tint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", c);
            mpb.SetColor("_Color", c);
            r.SetPropertyBlock(mpb);
        }

        // --- panel ----------------------------------------------------------

        private static void BuildPanel(Transform parent, ChatPanel panel,
                                       LlamaRunner runner, VoiceInput voice)
        {
            var canvasGo = new GameObject("ChatCanvas", typeof(Canvas),
                                          typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(parent, false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rt = canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            rt.localScale = Vector3.one * PanelScale;
            // Roughly eye height, 1.6 m out: close enough to read 24 pt text,
            // far enough to sit outside the comfortable-focus near limit.
            rt.localPosition = new Vector3(0.45f, 1.3f, 1.6f);

            var bg = NewChild(canvasGo.transform, "Background", typeof(Image));
            Stretch(bg.GetComponent<RectTransform>());
            bg.GetComponent<Image>().color = new Color(0.09f, 0.08f, 0.07f, 0.94f);

            var transcript = NewText(canvasGo.transform, "Transcript", 24,
                                     new Vector2(0, 60), new Vector2(PanelWidth - 60, 600));
            transcript.alignment = TextAlignmentOptions.TopLeft;
            transcript.richText = true;

            var status = NewText(canvasGo.transform, "Status", 18,
                                 new Vector2(0, -300), new Vector2(PanelWidth - 60, 40));
            status.alignment = TextAlignmentOptions.Left;
            status.color = new Color(0.66f, 0.61f, 0.53f);

            var field = NewInputField(canvasGo.transform, "PromptField",
                                      new Vector2(-160, -350), new Vector2(760, 64));

            var send   = NewButton(canvasGo.transform, "Send",   "Send",
                                   new Vector2(320, -350), new Vector2(150, 64));
            var mic    = NewButton(canvasGo.transform, "Mic",    "Hold to Talk",
                                   new Vector2(490, -350), new Vector2(180, 64));
            var cancel = NewButton(canvasGo.transform, "Cancel", "Stop",
                                   new Vector2(-460, -350), new Vector2(120, 64));
            var reset  = NewButton(canvasGo.transform, "Reset",  "Clear",
                                   new Vector2(-460, -270), new Vector2(120, 56));

            panel.transcriptText = transcript;
            panel.statusText     = status;
            panel.promptField    = field;
            panel.sendButton     = send;
            panel.micButton      = mic;
            panel.cancelButton   = cancel;
            panel.resetButton    = reset;
        }

        private static GameObject NewChild(Transform parent, string name, params System.Type[] comps)
        {
            var go = new GameObject(name, comps);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static TMP_Text NewText(Transform parent, string name, float size,
                                        Vector2 pos, Vector2 sizeDelta)
        {
            var go = NewChild(parent, name, typeof(TextMeshProUGUI));
            var t = go.GetComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.color = new Color(0.96f, 0.93f, 0.89f);
            t.text = string.Empty;
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;
            return t;
        }

        private static TMP_InputField NewInputField(Transform parent, string name,
                                                    Vector2 pos, Vector2 sizeDelta)
        {
            var go = NewChild(parent, name, typeof(Image), typeof(TMP_InputField));
            go.GetComponent<Image>().color = new Color(0.16f, 0.14f, 0.12f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;

            var textArea = NewChild(go.transform, "TextArea", typeof(RectMask2D));
            Stretch(textArea.GetComponent<RectTransform>());

            var text = NewText(textArea.transform, "Text", 22, Vector2.zero,
                               sizeDelta - new Vector2(24, 12));
            text.alignment = TextAlignmentOptions.Left;

            var placeholder = NewText(textArea.transform, "Placeholder", 22, Vector2.zero,
                                      sizeDelta - new Vector2(24, 12));
            placeholder.text = "Type, or hold the mic button…";
            placeholder.color = new Color(0.5f, 0.47f, 0.43f);
            placeholder.alignment = TextAlignmentOptions.Left;

            var field = go.GetComponent<TMP_InputField>();
            field.textViewport = textArea.GetComponent<RectTransform>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.lineType = TMP_InputField.LineType.SingleLine;
            return field;
        }

        private static Button NewButton(Transform parent, string name, string label,
                                        Vector2 pos, Vector2 sizeDelta)
        {
            var go = NewChild(parent, name, typeof(Image), typeof(Button));
            go.GetComponent<Image>().color = new Color(1f, 0.35f, 0f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;

            var text = NewText(go.transform, "Label", 20, Vector2.zero, sizeDelta);
            text.text = label;
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(0.1f, 0.07f, 0.04f);

            return go.GetComponent<Button>();
        }
    }
}
#endif
