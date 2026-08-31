// PointerHoldRelay.cs — turns Unity UI's pointer events into press/release
// callbacks, so a UI Button can act as push-to-talk.
//
// Button.onClick only fires on release and gives you no "held" edge, which is
// the wrong shape for dictation. This works with any input module that drives
// the EventSystem, including the ray interactors in Meta's Interaction SDK and
// XRI, because both ultimately dispatch these same pointer events.

using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestLlm.Runtime
{
    [DisallowMultipleComponent]
    public sealed class PointerHoldRelay : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public event Action OnPressed;
        public event Action OnReleased;

        public bool IsHeld { get; private set; }

        public void OnPointerDown(PointerEventData _)
        {
            if (IsHeld) return;
            IsHeld = true;
            OnPressed?.Invoke();
        }

        public void OnPointerUp(PointerEventData _) => Release();

        // A ray that slides off the button mid-hold would otherwise never
        // deliver the up event, leaving the mic recording indefinitely.
        public void OnPointerExit(PointerEventData _) => Release();

        private void OnDisable() => Release();

        private void Release()
        {
            if (!IsHeld) return;
            IsHeld = false;
            OnReleased?.Invoke();
        }
    }
}
