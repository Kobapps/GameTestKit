using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Human-shaped gestures on top of a raw <see cref="IInputBackend"/>: pointer moves are
    /// interpolated over several frames, presses hold for a realistic beat, drags ease in and out.
    /// </summary>
    /// <remarks>
    /// Every method is a coroutine and yields real frames between events — that is deliberate. A game
    /// that only sees a teleporting pointer and a same-frame press/release behaves differently from one
    /// driven by a person; tests written against instantaneous input pass while the shipped game is
    /// broken. Timings scale with <see cref="RunOptions.InputSpeedScale"/>.
    /// </remarks>
    public sealed class VirtualUser
    {
        private readonly RunOptions _options;

        public IInputBackend Backend { get; }

        /// <summary>Current pointer position in screen pixels.</summary>
        public Vector2 Pointer => Backend.PointerPosition;

        public PointerMode Mode => _options.Pointer;

        public VirtualUser(IInputBackend backend, RunOptions options)
        {
            Backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _options = options ?? new RunOptions();
        }

        private float Scale(float seconds) => seconds * Mathf.Max(0.01f, _options.InputSpeedScale);

        private void Require(InputCapability capability, string what)
        {
            if ((Backend.Capabilities & capability) == 0)
                throw new TestFailureException($"The {Backend.Name} backend cannot simulate {what}.");
        }

        // ---------------------------------------------------------------- pointer

        /// <summary>Moves the pointer to a screen point over a few frames (hover, not press).</summary>
        public IEnumerator MoveTo(Vector2 target, float duration = 0.08f)
        {
            Require(InputCapability.Pointer, "pointer movement");

            duration = Scale(duration);
            var start = Backend.PointerPosition;
            if (duration <= 0f || (target - start).sqrMagnitude < 1f)
            {
                Backend.PointerMove(target);
                InputOverlay.Pointer(target);
                Backend.Flush();
                yield return null;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Mathf.Max(Time.unscaledDeltaTime, 1f / 120f);
                float t = Mathf.Clamp01(elapsed / duration);
                var point = Vector2.Lerp(start, target, Smooth(t));
                Backend.PointerMove(point);
                InputOverlay.Pointer(point);
                Backend.Flush();
                yield return null;
            }

            Backend.PointerMove(target);
            InputOverlay.Pointer(target);
            Backend.Flush();
            yield return null;
        }

        /// <summary>Full click: move, press, hold a frame or two, release. Repeats for double clicks.</summary>
        public IEnumerator Click(Vector2 position, PointerButton button = PointerButton.Left, int clicks = 1)
        {
            Require(InputCapability.Pointer, "clicks");

            InputOverlay.Note($"{(Mode == PointerMode.Touch ? "tap" : "click")}" +
                              $"{(clicks > 1 ? " x" + clicks : "")}{ButtonSuffix(button)} {InputOverlay.Format(position)}");

            if (Mode == PointerMode.Mouse)
                yield return MoveTo(position);
            else
                Backend.PointerMove(position);

            for (int i = 0; i < Mathf.Max(1, clicks); i++)
            {
                Backend.PointerDown(position, button);
                InputOverlay.Press(position, button);
                Backend.Flush();
                yield return null;
                yield return null;

                Backend.PointerUp(position, button);
                InputOverlay.Release(position, button);
                Backend.Flush();
                yield return null;

                if (i + 1 < clicks)
                    yield return Wait.RealSeconds(Scale(0.06f));
            }
        }

        /// <summary>Press, wait, release — long-press / hold-to-charge interactions.</summary>
        public IEnumerator Hold(Vector2 position, float seconds, PointerButton button = PointerButton.Left)
        {
            Require(InputCapability.Pointer, "presses");

            InputOverlay.Note($"hold {seconds:0.##}s{ButtonSuffix(button)} {InputOverlay.Format(position)}");

            if (Mode == PointerMode.Mouse)
                yield return MoveTo(position);

            Backend.PointerDown(position, button);
            InputOverlay.Press(position, button);
            Backend.Flush();
            yield return null;

            yield return Wait.RealSeconds(seconds);

            Backend.PointerUp(position, button);
            InputOverlay.Release(position, button);
            Backend.Flush();
            yield return null;
        }

        /// <summary>
        /// Press at <paramref name="from"/>, travel to <paramref name="to"/> over
        /// <paramref name="duration"/> with easing, then release. Crosses the drag threshold gradually,
        /// so drag handlers see begin/drag/end exactly as with a real finger or mouse.
        /// </summary>
        public IEnumerator Drag(Vector2 from, Vector2 to, float duration = 0.35f,
            PointerButton button = PointerButton.Left, float settleSeconds = 0.05f)
        {
            return DragCore(from, to, duration, button, settleSeconds, "drag");
        }

        /// <summary>A flick: like <see cref="Drag"/> but fast and released while still moving.</summary>
        public IEnumerator Swipe(Vector2 from, Vector2 to, float duration = 0.15f)
        {
            return DragCore(from, to, duration, PointerButton.Left, 0f, "swipe");
        }

        /// <summary>The body both <see cref="Drag"/> and <see cref="Swipe"/> run; <paramref name="gesture"/> only names it for the overlay.</summary>
        private IEnumerator DragCore(Vector2 from, Vector2 to, float duration, PointerButton button,
            float settleSeconds, string gesture)
        {
            Require(InputCapability.Pointer, "drags");

            InputOverlay.Note($"{gesture} {InputOverlay.Format(from)} -> {InputOverlay.Format(to)}");

            duration = Scale(duration);

            if (Mode == PointerMode.Mouse)
                yield return MoveTo(from);
            else
                Backend.PointerMove(from);

            Backend.PointerDown(from, button);
            InputOverlay.Press(from, button);
            Backend.Flush();
            yield return null;
            yield return null;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Mathf.Max(Time.unscaledDeltaTime, 1f / 120f);
                float t = Mathf.Clamp01(elapsed / duration);
                var point = Vector2.Lerp(from, to, Smooth(t));
                Backend.PointerMove(point);
                InputOverlay.Pointer(point);
                Backend.Flush();
                yield return null;
            }

            Backend.PointerMove(to);
            InputOverlay.Pointer(to);
            Backend.Flush();
            yield return null;

            if (settleSeconds > 0f)
                yield return Wait.RealSeconds(Scale(settleSeconds));

            Backend.PointerUp(to, button);
            InputOverlay.Release(to, button);
            Backend.Flush();
            yield return null;
        }

        /// <summary>Scroll wheel / two-finger scroll. <paramref name="notches"/> is in wheel clicks.</summary>
        public IEnumerator Scroll(Vector2 position, Vector2 notches)
        {
            Require(InputCapability.Scroll, "scrolling");

            InputOverlay.Note($"scroll {notches.y:0.#}{(Mathf.Abs(notches.x) > 0.01f ? $" / {notches.x:0.#}" : "")} " +
                              $"at {InputOverlay.Format(position)}");

            if (Mode == PointerMode.Mouse)
                yield return MoveTo(position);

            Backend.Scroll(position, notches);
            InputOverlay.Ripple(position);
            Backend.Flush();
            yield return null;
            yield return null;
        }

        // ---------------------------------------------------------------- keyboard

        public IEnumerator PressKey(string key, float holdSeconds = 0.05f)
        {
            Require(InputCapability.Keys, "key presses");

            InputOverlay.Note($"key {key}");
            Backend.KeyDown(key);
            Backend.Flush();
            yield return null;

            if (holdSeconds > 0f)
                yield return Wait.RealSeconds(holdSeconds);

            Backend.KeyUp(key);
            Backend.Flush();
            yield return null;
        }

        public IEnumerator KeyDown(string key)
        {
            Require(InputCapability.Keys, "key presses");
            InputOverlay.Note($"key down {key}");
            Backend.KeyDown(key);
            Backend.Flush();
            yield return null;
        }

        public IEnumerator KeyUp(string key)
        {
            Require(InputCapability.Keys, "key presses");
            InputOverlay.Note($"key up {key}");
            Backend.KeyUp(key);
            Backend.Flush();
            yield return null;
        }

        /// <summary>Types a string one character at a time, the way an IME delivers text.</summary>
        public IEnumerator TypeText(string text, float perCharacterSeconds = 0.03f)
        {
            Require(InputCapability.Text, "text input");
            if (string.IsNullOrEmpty(text)) yield break;

            // The overlay echoes the string as it is delivered rather than all at once, so what is on
            // screen is what the game has actually received so far — the two diverge exactly when a
            // field is dropping characters, which is the bug worth seeing.
            InputOverlay.BeginText();

            foreach (var character in text)
            {
                if (character == '\n')
                {
                    Backend.KeyDown("enter"); Backend.Flush(); yield return null;
                    Backend.KeyUp("enter"); Backend.Flush();
                }
                else
                {
                    Backend.TextInput(character);
                    Backend.Flush();
                }
                InputOverlay.Character(character);
                yield return null;
                if (perCharacterSeconds > 0f)
                    yield return Wait.RealSeconds(Scale(perCharacterSeconds));
            }

            InputOverlay.EndText();
        }

        // ---------------------------------------------------------------- gamepad

        public IEnumerator GamepadPress(string button, float holdSeconds = 0.08f)
        {
            Require(InputCapability.Gamepad, "gamepad buttons");

            InputOverlay.Note($"gamepad {button}");
            Backend.GamepadButton(button, true);
            Backend.Flush();
            yield return null;

            if (holdSeconds > 0f) yield return Wait.RealSeconds(holdSeconds);

            Backend.GamepadButton(button, false);
            Backend.Flush();
            yield return null;
        }

        /// <summary>Holds a stick at <paramref name="value"/> for <paramref name="seconds"/>, then centres it.</summary>
        public IEnumerator GamepadStick(string stick, Vector2 value, float seconds)
        {
            Require(InputCapability.Gamepad, "gamepad sticks");

            InputOverlay.Note($"stick {stick} ({value.x:0.##},{value.y:0.##}) for {seconds:0.##}s");
            Backend.GamepadStick(stick, value);
            Backend.Flush();
            yield return null;

            if (seconds > 0f) yield return Wait.RealSeconds(seconds);

            if (seconds > 0f)
            {
                Backend.GamepadStick(stick, Vector2.zero);
                Backend.Flush();
                yield return null;
            }
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        /// <summary>" (right)" for the buttons worth naming in the overlay, nothing for a plain left click.</summary>
        private static string ButtonSuffix(PointerButton button) =>
            button == PointerButton.Left ? "" : $" ({button.ToString().ToLowerInvariant()})";
    }

    /// <summary>Chooses and constructs the right <see cref="IInputBackend"/> for a run.</summary>
    public static class InputBackendFactory
    {
        public static IInputBackend Create(RunOptions options)
        {
            var kind = options?.Backend ?? InputBackendKind.Auto;

            switch (kind)
            {
                case InputBackendKind.EventSystem:
                    return new EventSystemBackend();

                case InputBackendKind.InputSystem:
#if ENABLE_INPUT_SYSTEM
                    return new InputSystemBackend();
#else
                    throw new TestFailureException(
                        "RunOptions.Backend is InputSystem but the Input System backend is not enabled. " +
                        "Set Project Settings ▸ Player ▸ Active Input Handling to 'Input System Package' or 'Both'.");
#endif

                default:
#if ENABLE_INPUT_SYSTEM
                    // Device-level injection is strictly more capable; prefer it whenever the project
                    // is actually wired to the Input System.
                    if (UsesInputSystemUi() || EventSystem.current == null)
                        return new InputSystemBackend();
                    // A StandaloneInputModule means the UI is driven by the legacy backend, which
                    // cannot see injected Input System events — fall back to uGUI injection.
                    return new EventSystemBackend();
#else
                    return new EventSystemBackend();
#endif
            }
        }

        /// <summary>True when the active EventSystem is driven by the Input System UI module.</summary>
        public static bool UsesInputSystemUi()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) return false;
            var module = eventSystem.currentInputModule;
            if (module == null)
            {
                var modules = eventSystem.GetComponents<BaseInputModule>();
                for (int i = 0; i < modules.Length; i++)
                    if (IsInputSystemModule(modules[i])) return true;
                return false;
            }
            return IsInputSystemModule(module);
        }

        private static bool IsInputSystemModule(BaseInputModule module)
        {
            if (module == null) return false;
            return module.GetType().FullName == "UnityEngine.InputSystem.UI.InputSystemUIInputModule";
        }
    }
}
