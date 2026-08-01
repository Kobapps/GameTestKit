#if ENABLE_INPUT_SYSTEM
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Injects events into the Input System at the <em>device</em> level: the test creates virtual
    /// mouse / touchscreen / keyboard / gamepad devices and queues real state events on them.
    /// </summary>
    /// <remarks>
    /// Because the events enter the pipeline where the OS would put them, everything downstream
    /// behaves exactly as in a human session — <c>InputAction</c> callbacks, <c>PlayerInput</c>,
    /// interactions, processors, <c>InputSystemUIInputModule</c> raycasts, drag thresholds and
    /// <c>Mouse.current</c>/<c>Touchscreen.current</c> reads. Nothing in the game is aware of the test.
    /// <para>
    /// Caveat: reads through the legacy <c>UnityEngine.Input</c> API are served by the old backend and
    /// cannot see these events. Set <em>Project Settings ▸ Player ▸ Active Input Handling</em> to
    /// <em>Input System Package</em> (or drive UI-only tests through <see cref="EventSystemBackend"/>).
    /// </para>
    /// </remarks>
    public sealed class InputSystemBackend : IInputBackend
    {
        private const int TouchId = 1;
        private const float ScrollTicksPerNotch = 120f;

        private Mouse _mouse;
        private Touchscreen _touchscreen;
        private Keyboard _keyboard;
        private Gamepad _gamepad;

        private MouseState _mouseState;
        private GamepadState _gamepadState;
        private readonly HashSet<Key> _heldKeys = new HashSet<Key>();
        private readonly List<InputDevice> _disabledRealDevices = new List<InputDevice>();
        private readonly List<InputDevice> _ownedDevices = new List<InputDevice>();

        private Vector2 _pointer;
        private Vector2 _touchStart;
        private bool _touchDown;
        private bool _isolate;
        private PointerMode _mode = PointerMode.Mouse;

        private InputSettings.BackgroundBehavior _previousBackgroundBehavior;
        private bool _previousRunInBackground;
#if UNITY_EDITOR
        private InputSettings.EditorInputBehaviorInPlayMode _previousEditorBehavior;
#endif
        private bool _focusOverridden;

        public string Name => "InputSystem (device-level)";

        public InputCapability Capabilities =>
            InputCapability.Pointer | InputCapability.Scroll | InputCapability.Keys |
            InputCapability.Text | InputCapability.Gamepad | InputCapability.Touch;

        public Vector2 PointerPosition => _pointer;

        /// <summary>True when the Input System is the backend the game actually reads from.</summary>
        public static bool IsAvailable
        {
            get
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                // "Both" is configured: UnityEngine.Input still reads the old backend, but the new one
                // is live for anything using InputAction / InputSystemUIInputModule.
                return true;
#else
                return true;
#endif
            }
        }

        public void Begin(RunOptions options)
        {
            _mode = options?.Pointer ?? PointerMode.Mouse;
            _isolate = options != null && options.IsolateRealDevices;
            _pointer = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            TakeFocusOutOfTheEquation();

            if (_isolate) IsolateRealDevices();

            if (_mode == PointerMode.Touch) EnsureTouchscreen();
            else EnsureMouse();

            WarnIfUiCannotReceiveInput();
        }

        /// <summary>
        /// Stops window focus from deciding whether a test's input counts.
        /// </summary>
        /// <remarks>
        /// By default the Input System disables non-background devices when the application loses
        /// focus, and in the Editor it routes pointer and keyboard input only while the Game view has
        /// focus. Neither is acceptable for a test run: in batch mode nothing is ever focused, and in
        /// the Editor a developer who clicks another window mid-run would silently break the run.
        /// The previous values are restored in <see cref="End"/>.
        /// </remarks>
        private void TakeFocusOutOfTheEquation()
        {
            _previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            _previousRunInBackground = Application.runInBackground;
#if UNITY_EDITOR
            _previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            Application.runInBackground = true;
            _focusOverridden = true;
        }

        private void RestoreFocusBehaviour()
        {
            if (!_focusOverridden) return;
            _focusOverridden = false;

            InputSystem.settings.backgroundBehavior = _previousBackgroundBehavior;
            Application.runInBackground = _previousRunInBackground;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode = _previousEditorBehavior;
#endif
        }

        /// <summary>
        /// Checks the two setups in which perfectly correct injected events reach nothing, and says so
        /// up front — otherwise every UI step fails with "the panel never opened" and the real cause is
        /// three layers away.
        /// </summary>
        private static void WarnIfUiCannotReceiveInput()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) return;

            var module = eventSystem.GetComponent<InputSystemUIInputModule>();

            if (module == null)
            {
                if (eventSystem.GetComponent<StandaloneInputModule>() != null)
                    Debug.LogWarning(
                        "[GameTestKit] The EventSystem uses StandaloneInputModule, which reads the legacy input " +
                        "backend and cannot see injected Input System events. Replace it with " +
                        "InputSystemUIInputModule, or set the run's backend to eventSystem.");
                return;
            }

            if (module.actionsAsset == null)
                Debug.LogWarning(
                    "[GameTestKit] The InputSystemUIInputModule has no actions assigned, so it ignores every " +
                    "pointer event — real or simulated. Assign an Input Actions asset, or call " +
                    "AssignDefaultActions() on it (components added from code do not get default actions).");
        }

        public void End()
        {
            // Release anything still held so the game doesn't see a stuck button between tests.
            try
            {
                if (_touchDown) PointerUp(_pointer, PointerButton.Left);
                if (_mouse != null && _mouseState.buttons != 0)
                {
                    _mouseState.buttons = 0;
                    QueueMouse();
                }
                if (_heldKeys.Count > 0)
                {
                    _heldKeys.Clear();
                    QueueKeyboard();
                }
                if (_gamepad != null)
                {
                    _gamepadState = default;
                    InputSystem.QueueStateEvent(_gamepad, _gamepadState);
                }
                Flush();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameTestKit] Error releasing virtual input: {e.Message}");
            }

            for (int i = 0; i < _ownedDevices.Count; i++)
            {
                var device = _ownedDevices[i];
                if (device != null && device.added) InputSystem.RemoveDevice(device);
            }
            _ownedDevices.Clear();
            _mouse = null; _touchscreen = null; _keyboard = null; _gamepad = null;

            RestoreRealDevices();
            RestoreFocusBehaviour();
        }

        public void Dispose() => End();

        // ---------------------------------------------------------------- devices

        private T Acquire<T>(string name) where T : InputDevice
        {
            // Prefer a device the project already has, so `Mouse.current` and friends keep pointing at
            // the device the game is bound to. Only create one when nothing suitable exists.
            var existing = InputSystem.GetDevice<T>();
            if (existing != null && existing.enabled) return existing;

            var device = InputSystem.AddDevice<T>(name);
            _ownedDevices.Add(device);
            return device;
        }

        private void EnsureMouse()
        {
            if (_mouse == null)
            {
                _mouse = Acquire<Mouse>("GameTestKit Mouse");
                _mouseState = new MouseState { position = _pointer, displayIndex = 0 };
            }
        }

        private void EnsureTouchscreen()
        {
            if (_touchscreen == null) _touchscreen = Acquire<Touchscreen>("GameTestKit Touchscreen");
        }

        private void EnsureKeyboard()
        {
            if (_keyboard == null) _keyboard = Acquire<Keyboard>("GameTestKit Keyboard");
        }

        private void EnsureGamepad()
        {
            if (_gamepad == null) _gamepad = Acquire<Gamepad>("GameTestKit Gamepad");
        }

        private void IsolateRealDevices()
        {
            foreach (var device in InputSystem.devices)
            {
                if (!device.enabled || _ownedDevices.Contains(device)) continue;
                if (!(device is Pointer || device is Keyboard || device is Gamepad)) continue;
                InputSystem.DisableDevice(device);
                _disabledRealDevices.Add(device);
            }
        }

        private void RestoreRealDevices()
        {
            for (int i = 0; i < _disabledRealDevices.Count; i++)
            {
                var device = _disabledRealDevices[i];
                if (device != null && device.added) InputSystem.EnableDevice(device);
            }
            _disabledRealDevices.Clear();
        }

        // ---------------------------------------------------------------- pointer

        public void PointerMove(Vector2 screenPosition)
        {
            var delta = screenPosition - _pointer;
            _pointer = screenPosition;

            if (_mode == PointerMode.Touch)
            {
                if (!_touchDown) return; // touch has no hover
                EnsureTouchscreen();
                QueueTouch(UnityEngine.InputSystem.TouchPhase.Moved, delta);
                return;
            }

            EnsureMouse();
            _mouseState.position = screenPosition;
            _mouseState.delta = delta;
            QueueMouse();
        }

        public void PointerDown(Vector2 screenPosition, PointerButton button)
        {
            _pointer = screenPosition;

            if (_mode == PointerMode.Touch)
            {
                EnsureTouchscreen();
                _touchDown = true;
                _touchStart = screenPosition;
                QueueTouch(UnityEngine.InputSystem.TouchPhase.Began, Vector2.zero);
                return;
            }

            EnsureMouse();
            _mouseState.position = screenPosition;
            _mouseState.delta = Vector2.zero;
            _mouseState = _mouseState.WithButton(ToMouseButton(button), true);
            _mouseState.clickCount = 1;
            QueueMouse();
        }

        public void PointerUp(Vector2 screenPosition, PointerButton button)
        {
            _pointer = screenPosition;

            if (_mode == PointerMode.Touch)
            {
                if (!_touchDown) return;
                EnsureTouchscreen();
                QueueTouch(UnityEngine.InputSystem.TouchPhase.Ended, Vector2.zero);
                _touchDown = false;
                return;
            }

            EnsureMouse();
            _mouseState.position = screenPosition;
            _mouseState.delta = Vector2.zero;
            _mouseState = _mouseState.WithButton(ToMouseButton(button), false);
            QueueMouse();
        }

        public void Scroll(Vector2 screenPosition, Vector2 delta)
        {
            EnsureMouse();
            _pointer = screenPosition;
            _mouseState.position = screenPosition;
            _mouseState.scroll = delta * ScrollTicksPerNotch;
            QueueMouse();
            // Scroll is a per-frame delta: clear it so it doesn't repeat next event.
            _mouseState.scroll = Vector2.zero;
        }

        private void QueueMouse() => InputSystem.QueueStateEvent(_mouse, _mouseState);

        private void QueueTouch(UnityEngine.InputSystem.TouchPhase phase, Vector2 delta)
        {
            InputSystem.QueueStateEvent(_touchscreen, new TouchState
            {
                touchId = TouchId,
                phase = phase,
                position = _pointer,
                delta = delta,
                startPosition = _touchStart,
                pressure = 1f,
                radius = new Vector2(8f, 8f),
                tapCount = 1,
            });
        }

        private static MouseButton ToMouseButton(PointerButton button)
        {
            switch (button)
            {
                case PointerButton.Right: return MouseButton.Right;
                case PointerButton.Middle: return MouseButton.Middle;
                default: return MouseButton.Left;
            }
        }

        // ---------------------------------------------------------------- keyboard

        public void KeyDown(string key)
        {
            EnsureKeyboard();
            _heldKeys.Add(ParseKey(key));
            QueueKeyboard();
        }

        public void KeyUp(string key)
        {
            EnsureKeyboard();
            _heldKeys.Remove(ParseKey(key));
            QueueKeyboard();
        }

        public void TextInput(char character)
        {
            EnsureKeyboard();
            InputSystem.QueueTextEvent(_keyboard, character);
        }

        private void QueueKeyboard()
        {
            var keys = new Key[_heldKeys.Count];
            _heldKeys.CopyTo(keys);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(keys));
        }

        internal static Key ParseKey(string key)
        {
            key = KeyNames.Normalize(key);
            if (string.IsNullOrEmpty(key))
                throw new TestFailureException("Key name is empty.");

            if (key.Length == 1 && key[0] >= '0' && key[0] <= '9')
                key = "digit" + key;
            if (key.Length == 1 && key[0] >= 'a' && key[0] <= 'z')
                key = key.ToUpperInvariant();

            if (Enum.TryParse(key, true, out Key parsed))
                return parsed;

            throw new TestFailureException(
                $"Unknown key '{key}'. Use Input System key names such as space, enter, escape, a, digit1, f5, leftshift, uparrow.");
        }

        // ---------------------------------------------------------------- gamepad

        public void GamepadButton(string button, bool pressed)
        {
            EnsureGamepad();
            var normalized = (button ?? "").Trim().ToLowerInvariant();

            if (normalized == "lefttrigger" || normalized == "l2")
            {
                _gamepadState.leftTrigger = pressed ? 1f : 0f;
            }
            else if (normalized == "righttrigger" || normalized == "r2")
            {
                _gamepadState.rightTrigger = pressed ? 1f : 0f;
            }
            else
            {
                if (!Enum.TryParse(normalized, true, out GamepadButton parsed))
                    throw new TestFailureException(
                        $"Unknown gamepad button '{button}'. Try south/north/east/west, a/b/x/y, start, select, " +
                        "leftShoulder, rightShoulder, dpadUp, leftTrigger, rightTrigger.");
                _gamepadState = _gamepadState.WithButton(parsed, pressed);
            }

            InputSystem.QueueStateEvent(_gamepad, _gamepadState);
        }

        public void GamepadStick(string stick, Vector2 value)
        {
            EnsureGamepad();
            var normalized = (stick ?? "left").Trim().ToLowerInvariant();
            value = Vector2.ClampMagnitude(value, 1f);

            if (normalized.StartsWith("r")) _gamepadState.rightStick = value;
            else _gamepadState.leftStick = value;

            InputSystem.QueueStateEvent(_gamepad, _gamepadState);
        }

        // ---------------------------------------------------------------- flush

        public void Flush()
        {
            // In the default update modes the queued events are consumed by the engine's own update,
            // which is what we want: the test observes exactly the timing a player would produce.
            // Only manual mode needs a nudge.
            if (InputSystem.settings.updateMode == InputSettings.UpdateMode.ProcessEventsManually)
                InputSystem.Update();
        }
    }
}
#endif
