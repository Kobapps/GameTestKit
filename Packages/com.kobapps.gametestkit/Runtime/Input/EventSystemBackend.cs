using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Synthesises pointer input at the uGUI <see cref="EventSystem"/> level: raycasts the canvas
    /// stack at a screen point and dispatches the same enter/down/drag/up/click messages the real
    /// input module would.
    /// </summary>
    /// <remarks>
    /// Works regardless of which input backend the project uses (including the legacy Input Manager),
    /// which makes it the portable fallback for UI-driven flows. It does <em>not</em> reach code that
    /// reads devices directly (<c>Input.GetKey</c>, <c>InputAction</c>, camera controllers, physics
    /// raycasts from the game's own pointer code) — use <see cref="InputSystemBackend"/> for those.
    /// </remarks>
    public sealed class EventSystemBackend : IInputBackend
    {
        private sealed class ButtonState
        {
            public PointerEventData Data;
            public bool IsDown;
            public GameObject Dragging;
        }

        private readonly Dictionary<PointerButton, ButtonState> _buttons =
            new Dictionary<PointerButton, ButtonState>();

        private readonly List<RaycastResult> _raycasts = new List<RaycastResult>();
        private readonly List<GameObject> _hovered = new List<GameObject>();
        private Vector2 _pointer;

        public string Name => "EventSystem (uGUI injection)";

        public InputCapability Capabilities =>
            InputCapability.Pointer | InputCapability.Scroll | InputCapability.Touch;

        public Vector2 PointerPosition => _pointer;

        public static bool IsAvailable => EventSystem.current != null;

        public void Begin(RunOptions options)
        {
            if (EventSystem.current == null)
                throw new TestFailureException(
                    "The EventSystem input backend needs an active EventSystem in the scene. " +
                    "Add one (GameObject ▸ UI ▸ Event System) or switch to the InputSystem backend.");
            _pointer = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }

        public void End()
        {
            foreach (var kv in _buttons)
                if (kv.Value.IsDown) PointerUp(_pointer, kv.Key);

            ClearHover();
            _buttons.Clear();
        }

        public void Dispose() => End();

        // ---------------------------------------------------------------- helpers

        private ButtonState GetState(PointerButton button)
        {
            if (_buttons.TryGetValue(button, out var state)) return state;

            state = new ButtonState
            {
                Data = new PointerEventData(EventSystem.current)
                {
                    pointerId = -1 - (int)button,   // matches uGUI's mouse pointer id convention
                    button = ToInputButton(button),
                },
            };
            _buttons[button] = state;
            return state;
        }

        private static PointerEventData.InputButton ToInputButton(PointerButton button)
        {
            switch (button)
            {
                case PointerButton.Right: return PointerEventData.InputButton.Right;
                case PointerButton.Middle: return PointerEventData.InputButton.Middle;
                default: return PointerEventData.InputButton.Left;
            }
        }

        private RaycastResult Raycast(PointerEventData data)
        {
            _raycasts.Clear();
            if (EventSystem.current == null) return default;
            EventSystem.current.RaycastAll(data, _raycasts);
            return _raycasts.Count > 0 ? _raycasts[0] : default;
        }

        /// <summary>Raycasts the UI at a screen point without disturbing pointer state.</summary>
        public static GameObject RaycastTopmost(Vector2 screenPosition)
        {
            if (EventSystem.current == null) return null;
            var data = new PointerEventData(EventSystem.current) { position = screenPosition };
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, results);
            return results.Count > 0 ? results[0].gameObject : null;
        }

        private void UpdateHover(PointerEventData data, GameObject target)
        {
            // Exit anything no longer under the pointer, enter anything newly under it.
            for (int i = _hovered.Count - 1; i >= 0; i--)
            {
                var go = _hovered[i];
                if (go == null || target == null || !IsAncestorOrSelf(go, target))
                {
                    if (go != null) ExecuteEvents.Execute(go, data, ExecuteEvents.pointerExitHandler);
                    _hovered.RemoveAt(i);
                }
            }

            if (target == null) return;

            var t = target.transform;
            while (t != null)
            {
                var go = t.gameObject;
                if (!_hovered.Contains(go))
                {
                    ExecuteEvents.Execute(go, data, ExecuteEvents.pointerEnterHandler);
                    _hovered.Add(go);
                }
                t = t.parent;
            }
        }

        private void ClearHover()
        {
            var data = new PointerEventData(EventSystem.current) { position = _pointer };
            for (int i = _hovered.Count - 1; i >= 0; i--)
                if (_hovered[i] != null)
                    ExecuteEvents.Execute(_hovered[i], data, ExecuteEvents.pointerExitHandler);
            _hovered.Clear();
        }

        private static bool IsAncestorOrSelf(GameObject ancestor, GameObject candidate)
        {
            var t = candidate.transform;
            while (t != null)
            {
                if (t.gameObject == ancestor) return true;
                t = t.parent;
            }
            return false;
        }

        // ---------------------------------------------------------------- pointer

        public void PointerMove(Vector2 screenPosition)
        {
            var delta = screenPosition - _pointer;
            _pointer = screenPosition;

            // Hover follows the primary (left) pointer, matching uGUI's mouse behaviour.
            var hover = GetState(PointerButton.Left);
            hover.Data.position = screenPosition;
            hover.Data.delta = delta;
            var hit = Raycast(hover.Data);
            hover.Data.pointerCurrentRaycast = hit;
            UpdateHover(hover.Data, hit.gameObject);

            foreach (var kv in _buttons)
            {
                var state = kv.Value;
                if (!state.IsDown) continue;

                state.Data.position = screenPosition;
                state.Data.delta = delta;
                state.Data.pointerCurrentRaycast = state == hover ? hit : Raycast(state.Data);

                if (state.Data.pointerDrag == null) continue;

                if (!state.Data.dragging)
                {
                    float threshold = EventSystem.current.pixelDragThreshold;
                    if ((screenPosition - state.Data.pressPosition).sqrMagnitude >= threshold * threshold)
                    {
                        state.Data.dragging = true;
                        state.Data.eligibleForClick = false;
                        state.Dragging = state.Data.pointerDrag;
                        ExecuteEvents.Execute(state.Dragging, state.Data, ExecuteEvents.beginDragHandler);
                    }
                }
                else
                {
                    ExecuteEvents.Execute(state.Dragging, state.Data, ExecuteEvents.dragHandler);
                }
            }
        }

        public void PointerDown(Vector2 screenPosition, PointerButton button)
        {
            _pointer = screenPosition;
            var state = GetState(button);
            var data = state.Data;

            data.position = screenPosition;
            data.delta = Vector2.zero;
            var hit = Raycast(data);
            data.pointerCurrentRaycast = hit;
            data.pointerPressRaycast = hit;
            data.pressPosition = screenPosition;
            data.clickTime = Time.unscaledTime;
            data.eligibleForClick = true;
            data.dragging = false;
            data.useDragThreshold = true;
            data.clickCount = 1;

            UpdateHover(data, hit.gameObject);

            var target = hit.gameObject;
            if (target != null)
            {
                var press = ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerDownHandler)
                            ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);

                data.pointerPress = press;
                data.rawPointerPress = target;
                data.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(target);

                if (button == PointerButton.Left)
                {
                    var selectable = ExecuteEvents.GetEventHandler<ISelectHandler>(target);
                    EventSystem.current.SetSelectedGameObject(selectable, data);
                }
            }
            else
            {
                data.pointerPress = null;
                data.rawPointerPress = null;
                data.pointerDrag = null;
                if (button == PointerButton.Left)
                    EventSystem.current.SetSelectedGameObject(null, data);
            }

            state.IsDown = true;
        }

        public void PointerUp(Vector2 screenPosition, PointerButton button)
        {
            _pointer = screenPosition;
            if (!_buttons.TryGetValue(button, out var state) || !state.IsDown) return;

            var data = state.Data;
            data.position = screenPosition;
            var hit = Raycast(data);
            data.pointerCurrentRaycast = hit;

            if (data.pointerPress != null)
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);

            var clickHandler = hit.gameObject != null
                ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject)
                : null;

            if (data.eligibleForClick && clickHandler != null && clickHandler == data.pointerPress)
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerClickHandler);

            if (data.dragging && state.Dragging != null)
            {
                if (hit.gameObject != null)
                    ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.dropHandler);
                ExecuteEvents.Execute(state.Dragging, data, ExecuteEvents.endDragHandler);
            }

            data.dragging = false;
            data.eligibleForClick = false;
            data.pointerPress = null;
            data.rawPointerPress = null;
            data.pointerDrag = null;
            state.Dragging = null;
            state.IsDown = false;
        }

        public void Scroll(Vector2 screenPosition, Vector2 delta)
        {
            _pointer = screenPosition;
            var state = GetState(PointerButton.Left);
            state.Data.position = screenPosition;
            var hit = Raycast(state.Data);
            state.Data.pointerCurrentRaycast = hit;
            state.Data.scrollDelta = delta;

            if (hit.gameObject != null)
                ExecuteEvents.ExecuteHierarchy(hit.gameObject, state.Data, ExecuteEvents.scrollHandler);

            state.Data.scrollDelta = Vector2.zero;
        }

        // ---------------------------------------------------------------- unsupported

        private static Exception Unsupported(string what) => new TestFailureException(
            $"The EventSystem backend cannot simulate {what}. Switch the run to the InputSystem backend " +
            "(RunOptions.Backend = InputSystem, or \"backend\": \"inputSystem\" in the suite) and make sure " +
            "Active Input Handling includes the Input System package.");

        public void KeyDown(string key) => throw Unsupported("key presses");

        public void KeyUp(string key) => throw Unsupported("key presses");

        public void TextInput(char character) => throw Unsupported("text input");

        public void GamepadButton(string button, bool pressed) => throw Unsupported("gamepad buttons");

        public void GamepadStick(string stick, Vector2 value) => throw Unsupported("gamepad sticks");

        public void Flush() { }
    }
}
