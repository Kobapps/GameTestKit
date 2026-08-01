using System;
using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Watches a human play and writes what they did as a <c>.gametest.json</c> script.
    /// </summary>
    /// <remarks>
    /// Recording is the fastest honest way to get a first test: play the flow once, then edit the
    /// generated script. Every click is resolved to the most stable selector available for what was hit
    /// — a <see cref="TestId"/> if there is one, then the visible label, then the name, then the path —
    /// so a recording does not hard-code hierarchy positions that will move next sprint.
    /// <para>Works in the Editor and in a development build, so QA can record on a device.</para>
    /// </remarks>
    public sealed class TestRecorder
    {
        /// <summary>Pointer travel (in pixels) above which a press/release becomes a drag rather than a click.</summary>
        public float DragThreshold = 12f;

        /// <summary>Pauses longer than this are written out as explicit wait steps.</summary>
        public float PauseThreshold = 0.4f;

        /// <summary>Insert an assertion for the element under the pointer.</summary>
        public string AssertHotkey = "f8";

        /// <summary>Insert a screenshot step.</summary>
        public string ScreenshotHotkey = "f9";

        public static TestRecorder Active { get; private set; }

        public bool IsRecording { get; private set; }

        public int StepCount => _steps.Count;

        /// <summary>Raised whenever a step is appended, so a window can show the script growing live.</summary>
        public event Action<string> StepRecorded;

        private readonly List<JsonValue> _steps = new List<JsonValue>();
        private string _name = "recorded flow";
        private string _startScene;
        private float _lastActionTime;

        private bool _pointerDown;
        private Vector2 _pressPosition;
        private string _pressSelector;
        private float _pressTime;

        private readonly HashSet<string> _heldKeys = new HashSet<string>();
        private string _lastScene;

        // ---------------------------------------------------------------- lifecycle

        public static TestRecorder Begin(string name = null)
        {
            Stop();

            Active = new TestRecorder();
            Active.StartRecording(name);
            RecorderHost.Ensure();
            return Active;
        }

        /// <summary>Stops the active recorder and returns the generated script, or null if none was running.</summary>
        public static string Stop()
        {
            if (Active == null || !Active.IsRecording) return null;

            var json = Active.Finish();
            Active = null;
            RecorderHost.Dispose();
            return json;
        }

        private void StartRecording(string name)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "recorded flow" : name.Trim();
            _steps.Clear();
            _startScene = SceneManager.GetActiveScene().name;
            _lastScene = _startScene;
            _lastActionTime = Time.realtimeSinceStartup;
            IsRecording = true;
        }

        /// <summary>Ends recording and renders the script.</summary>
        public string Finish()
        {
            IsRecording = false;

            var root = JsonValue.NewObject()
                .Set("name", _name)
                .Set("description", "Recorded from live play. Review the selectors and replace waits with waitFor.");

            var tags = JsonValue.NewArray();
            tags.Add(JsonValue.New("recorded"));
            root.Set("tags", tags);

            if (!string.IsNullOrEmpty(_startScene)) root.Set("scene", _startScene);

            var steps = JsonValue.NewArray();
            foreach (var step in _steps) steps.Add(step);
            root.Set("steps", steps);

            return root.ToJson();
        }

        /// <summary>Appends a hand-written step, e.g. from a recorder UI button.</summary>
        public void Append(JsonValue step)
        {
            if (step == null) return;
            _steps.Add(step);
            StepRecorded?.Invoke(step.ToJson(false));
        }

        // ---------------------------------------------------------------- capture

        internal void Tick()
        {
            if (!IsRecording) return;

            TrackSceneChange();

#if ENABLE_INPUT_SYSTEM
            TrackPointer();
            TrackKeyboard();
#endif
        }

        private void TrackSceneChange()
        {
            var scene = SceneManager.GetActiveScene().name;
            if (scene == _lastScene) return;

            _lastScene = scene;
            Append(JsonValue.NewObject().Set("waitForScene", scene).Set("timeout", 30));
            _lastActionTime = Time.realtimeSinceStartup;
        }

        private void InsertPauseIfNeeded()
        {
            float gap = Time.realtimeSinceStartup - _lastActionTime;
            if (gap >= PauseThreshold)
                _steps.Add(JsonValue.NewObject().Set("wait", Math.Round(gap, 1)));
            _lastActionTime = Time.realtimeSinceStartup;
        }

#if ENABLE_INPUT_SYSTEM
        private void TrackPointer()
        {
            Vector2 position;
            bool pressed;

            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.isPressed)
            {
                position = touch.primaryTouch.position.ReadValue();
                pressed = true;
            }
            else if (touch != null && _pointerDown && !touch.primaryTouch.press.isPressed && Mouse.current == null)
            {
                position = touch.primaryTouch.position.ReadValue();
                pressed = false;
            }
            else
            {
                var mouse = Mouse.current;
                if (mouse == null) return;
                position = mouse.position.ReadValue();
                pressed = mouse.leftButton.isPressed;
            }

            if (pressed && !_pointerDown)
            {
                _pointerDown = true;
                _pressPosition = position;
                _pressTime = Time.realtimeSinceStartup;
                _pressSelector = SelectorAt(position);
                return;
            }

            if (!pressed && _pointerDown)
            {
                _pointerDown = false;
                float travel = Vector2.Distance(position, _pressPosition);
                float held = Time.realtimeSinceStartup - _pressTime;

                InsertPauseIfNeeded();

                if (travel > DragThreshold)
                {
                    Append(JsonValue.NewObject()
                        .Set("drag", _pressSelector)
                        .Set("to", SelectorAt(position))
                        .Set("duration", Math.Max(0.1, Math.Round(held, 2))));
                }
                else if (held > 0.6f)
                {
                    Append(JsonValue.NewObject()
                        .Set("hold", _pressSelector)
                        .Set("seconds", Math.Round(held, 2)));
                }
                else
                {
                    Append(JsonValue.NewObject().Set("click", _pressSelector));
                }
            }

            var scroll = Mouse.current?.scroll.ReadValue() ?? Vector2.zero;
            if (Mathf.Abs(scroll.y) > 0.5f)
            {
                InsertPauseIfNeeded();
                Append(JsonValue.NewObject()
                    .Set("scroll", Math.Round(scroll.y / 120f, 1))
                    .Set("over", SelectorAt(Mouse.current.position.ReadValue())));
            }
        }

        private void TrackKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            foreach (var control in keyboard.allKeys)
            {
                var name = control.name;

                if (control.wasPressedThisFrame && _heldKeys.Add(name))
                {
                    if (HandleHotkey(name)) continue;

                    InsertPauseIfNeeded();
                    Append(JsonValue.NewObject().Set("press", name));
                }
                else if (control.wasReleasedThisFrame)
                {
                    _heldKeys.Remove(name);
                }
            }
        }

        private bool HandleHotkey(string key)
        {
            if (string.Equals(key, AssertHotkey, StringComparison.OrdinalIgnoreCase))
            {
                var pointer = Mouse.current?.position.ReadValue()
                              ?? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                var selector = SelectorAt(pointer);
                var target = Locator.Find(selector);
                var text = target != null ? UiProbe.LabelOf(target) : null;

                Append(string.IsNullOrWhiteSpace(text)
                    ? JsonValue.NewObject().Set("assertVisible", selector)
                    : JsonValue.NewObject().Set("assertText", selector).Set("equals", text.Trim()));
                return true;
            }

            if (string.Equals(key, ScreenshotHotkey, StringComparison.OrdinalIgnoreCase))
            {
                Append(JsonValue.NewObject().Set("screenshot", $"step-{_steps.Count + 1}"));
                return true;
            }

            return false;
        }
#endif

        /// <summary>The most stable selector for whatever sits at a screen point.</summary>
        public static string SelectorAt(Vector2 screenPosition)
        {
            var ui = EventSystemBackend.RaycastTopmost(screenPosition);
            if (ui != null)
            {
                var owner = Locator.ClickableOwnerOf(ui);
                var suggestions = Locator.SuggestSelectorsFor(owner);
                if (suggestions.Count > 0) return suggestions[0];
            }

            var camera = Camera.main;
            if (camera != null)
            {
                var ray = camera.ScreenPointToRay(screenPosition);
                if (Physics.Raycast(ray, out var hit, 1000f))
                {
                    var suggestions = Locator.SuggestSelectorsFor(hit.collider.gameObject);
                    if (suggestions.Count > 0) return suggestions[0];
                }

                var hit2d = Physics2D.GetRayIntersection(ray, 1000f);
                if (hit2d.collider != null)
                {
                    var suggestions = Locator.SuggestSelectorsFor(hit2d.collider.gameObject);
                    if (suggestions.Count > 0) return suggestions[0];
                }
            }

            // Nothing identifiable: fall back to a normalised screen point, which at least replays.
            return $"pos:{screenPosition.x / Mathf.Max(1, Screen.width):0.###},{screenPosition.y / Mathf.Max(1, Screen.height):0.###}";
        }

        /// <summary>Drives <see cref="TestRecorder.Tick"/> every frame while recording.</summary>
        private sealed class RecorderHost : MonoBehaviour
        {
            private static RecorderHost _instance;

            public static void Ensure()
            {
                if (_instance != null) return;
                var go = new GameObject("~GameTestKit.Recorder") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<RecorderHost>();
            }

            public static void Dispose()
            {
                if (_instance == null) return;
                Destroy(_instance.gameObject);
                _instance = null;
            }

            private void Update() => Active?.Tick();
        }
    }
}
