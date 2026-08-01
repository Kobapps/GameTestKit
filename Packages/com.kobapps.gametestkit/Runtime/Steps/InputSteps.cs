using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Kobapps.GameTestKit
{
    /// <summary>Shared plumbing for steps that need a screen point from a selector.</summary>
    internal static class StepTargeting
    {
        /// <summary>
        /// Resolves <paramref name="selector"/> and computes the exact pixel a gesture should use:
        /// the element centre by default, or a normalised point inside its rect plus a pixel offset.
        /// </summary>
        public static IEnumerator PointOf(TestContext ctx, string selector, ResolvedTarget result,
            Vector2? normalizedAt, Vector2 offset, LocatorEngine.Requirement requirement, float? timeout)
        {
            yield return ctx.Locate.Resolve(selector, result, requirement, timeout);

            if (result.HasGameObject)
            {
                if (normalizedAt.HasValue)
                {
                    var rect = UiProbe.ScreenRectOf(result.GameObject);
                    result.ScreenPoint = new Vector2(
                        rect.x + rect.width * normalizedAt.Value.x,
                        rect.y + rect.height * normalizedAt.Value.y);
                }
                else
                {
                    result.ScreenPoint = UiProbe.ScreenPointOf(result.GameObject);
                }
            }

            result.ScreenPoint += offset;
        }

        public static LocatorEngine.Requirement ClickRequirement(bool force) =>
            force ? LocatorEngine.Requirement.Visible : LocatorEngine.Requirement.Clickable;
    }

    /// <summary>Presses on an element or a screen point, the way a player taps or clicks it.</summary>
    public sealed class ClickStep : TestStep
    {
        public string Selector;
        public PointerButton Button = PointerButton.Left;
        public int Clicks = 1;
        public Vector2? NormalizedAt;
        public Vector2 Offset;

        /// <summary>Skip the "nothing is covering it" check — for deliberately testing blocked input.</summary>
        public bool Force;

        public override string Describe() =>
            Clicks > 1 ? $"click {Selector} ×{Clicks}" : $"click {Selector}";

        public override IEnumerator Execute(TestContext ctx)
        {
            var target = new ResolvedTarget();
            yield return StepTargeting.PointOf(ctx, Selector, target, NormalizedAt, Offset,
                StepTargeting.ClickRequirement(Force), TimeoutSeconds);

            yield return ctx.User.Click(target.ScreenPoint, Button, Clicks);
        }
    }

    /// <summary>Press, wait, release — long press, hold-to-charge, context menus on touch.</summary>
    public sealed class HoldStep : TestStep
    {
        public string Selector;
        public float Seconds = 1f;
        public PointerButton Button = PointerButton.Left;
        public bool Force;

        public override string Describe() => $"hold {Selector} for {Seconds:0.##}s";

        public override IEnumerator Execute(TestContext ctx)
        {
            var target = new ResolvedTarget();
            yield return StepTargeting.PointOf(ctx, Selector, target, null, Vector2.zero,
                StepTargeting.ClickRequirement(Force), TimeoutSeconds);

            yield return ctx.User.Hold(target.ScreenPoint, Seconds, Button);
        }
    }

    /// <summary>Moves the pointer over an element without pressing — hover states, tooltips.</summary>
    public sealed class MoveStep : TestStep
    {
        public string Selector;
        public float Duration = 0.08f;

        public override string Describe() => $"move to {Selector}";

        public override IEnumerator Execute(TestContext ctx)
        {
            var target = new ResolvedTarget();
            yield return StepTargeting.PointOf(ctx, Selector, target, null, Vector2.zero,
                LocatorEngine.Requirement.Visible, TimeoutSeconds);

            yield return ctx.User.MoveTo(target.ScreenPoint, Duration);
        }
    }

    /// <summary>Drags one element onto another (or onto a point) with a real, eased pointer path.</summary>
    public sealed class DragStep : TestStep
    {
        public string FromSelector;
        public string ToSelector;
        public Vector2 ToOffset;
        public float Duration = 0.35f;
        public PointerButton Button = PointerButton.Left;
        public bool Force;

        public override string Describe() => $"drag {FromSelector} → {ToSelector}";

        public override IEnumerator Execute(TestContext ctx)
        {
            var from = new ResolvedTarget();
            yield return StepTargeting.PointOf(ctx, FromSelector, from, null, Vector2.zero,
                StepTargeting.ClickRequirement(Force), TimeoutSeconds);

            var to = new ResolvedTarget();
            yield return StepTargeting.PointOf(ctx, ToSelector, to, null, ToOffset,
                LocatorEngine.Requirement.Exists, TimeoutSeconds);

            yield return ctx.User.Drag(from.ScreenPoint, to.ScreenPoint, Duration, Button);
        }
    }

    /// <summary>A flick in a direction — carousels, inventory pages, camera pans.</summary>
    public sealed class SwipeStep : TestStep
    {
        public string Direction = "left";     // left | right | up | down
        public string FromSelector;           // optional; defaults to the screen centre
        public float Distance;                // pixels; 0 picks 40% of the screen
        public float Duration = 0.15f;

        public override string Describe() =>
            $"swipe {Direction}{(string.IsNullOrEmpty(FromSelector) ? "" : $" from {FromSelector}")}";

        public override IEnumerator Execute(TestContext ctx)
        {
            Vector2 origin;
            if (string.IsNullOrEmpty(FromSelector))
            {
                origin = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            }
            else
            {
                var target = new ResolvedTarget();
                yield return StepTargeting.PointOf(ctx, FromSelector, target, null, Vector2.zero,
                    LocatorEngine.Requirement.Visible, TimeoutSeconds);
                origin = target.ScreenPoint;
            }

            Vector2 direction;
            switch ((Direction ?? "left").Trim().ToLowerInvariant())
            {
                case "right": direction = Vector2.right; break;
                case "up": direction = Vector2.up; break;
                case "down": direction = Vector2.down; break;
                case "left": direction = Vector2.left; break;
                default:
                    throw new TestFailureException($"Swipe direction '{Direction}' must be left, right, up or down.");
            }

            float distance = Distance > 0f
                ? Distance
                : (direction.x != 0f ? Screen.width : Screen.height) * 0.4f;

            var destination = origin + direction * distance;
            destination.x = Mathf.Clamp(destination.x, 1f, Screen.width - 1f);
            destination.y = Mathf.Clamp(destination.y, 1f, Screen.height - 1f);

            yield return ctx.User.Swipe(origin, destination, Duration);
        }
    }

    /// <summary>Mouse wheel / trackpad scroll over an element. Negative amounts scroll down.</summary>
    public sealed class ScrollStep : TestStep
    {
        public float Amount = -3f;
        public float Horizontal;
        public string Selector;

        public override string Describe() =>
            $"scroll {Amount:0.#}{(string.IsNullOrEmpty(Selector) ? "" : $" over {Selector}")}";

        public override IEnumerator Execute(TestContext ctx)
        {
            Vector2 point;
            if (string.IsNullOrEmpty(Selector))
            {
                point = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            }
            else
            {
                var target = new ResolvedTarget();
                yield return StepTargeting.PointOf(ctx, Selector, target, null, Vector2.zero,
                    LocatorEngine.Requirement.Visible, TimeoutSeconds);
                point = target.ScreenPoint;
            }

            yield return ctx.User.Scroll(point, new Vector2(Horizontal, Amount));
        }
    }

    /// <summary>Types text as a sequence of real character events, optionally focusing a field first.</summary>
    public sealed class TypeTextStep : TestStep
    {
        public string Text = "";
        public string IntoSelector;
        public bool Clear;
        public float PerCharacterSeconds = 0.03f;

        public override string Describe() =>
            string.IsNullOrEmpty(IntoSelector) ? $"type \"{Text}\"" : $"type \"{Text}\" into {IntoSelector}";

        public override IEnumerator Execute(TestContext ctx)
        {
            GameObject field = null;

            if (!string.IsNullOrEmpty(IntoSelector))
            {
                var target = new ResolvedTarget();
                yield return StepTargeting.PointOf(ctx, IntoSelector, target, null, Vector2.zero,
                    LocatorEngine.Requirement.Clickable, TimeoutSeconds);

                field = target.GameObject;
                yield return ctx.User.Click(target.ScreenPoint);
                yield return null;
            }

            if (Clear && field != null)
                ClearField(field);

            if ((ctx.User.Backend.Capabilities & InputCapability.Text) != 0)
            {
                yield return ctx.User.TypeText(Text, PerCharacterSeconds);
                yield return null;

                // uGUI input fields read typed characters through the *legacy* input backend, which
                // cannot see injected Input System text events. Rather than fail a correct test on an
                // engine-side gap, check whether the field actually got the text and, if not, feed it
                // the same characters through its own key handling — validation, character limits and
                // content type still apply.
                if (field != null && !FieldContains(field, Text))
                {
                    if (TryTypeIntoComponent(field, Text, clearFirst: true))
                    {
                        InputOverlay.Text(Text);
                        Debug.LogWarning(
                            $"[GameTestKit] Simulated text events did not reach '{IntoSelector}'. uGUI input " +
                            "fields read text via the legacy input backend, so the field was fed directly " +
                            "instead. Pointer, key and gamepad input are unaffected.");
                        yield return null;
                    }
                }
            }
            else if (field != null && TryTypeIntoComponent(field, Text, clearFirst: false))
            {
                InputOverlay.Text(Text);
                yield return null;
            }
            else
            {
                throw new TestFailureException(
                    $"The {ctx.User.Backend.Name} backend cannot type text, and '{IntoSelector}' is not an " +
                    "InputField/TMP_InputField that could receive it directly. Use the InputSystem backend.");
            }
        }

        /// <summary>True when the field already shows the text we typed.</summary>
        private static bool FieldContains(GameObject field, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;

            var legacy = field.GetComponentInChildren<InputField>();
            if (legacy != null) return legacy.text != null && legacy.text.Contains(text);

            var tmp = field.GetComponentInChildren<TMPro.TMP_InputField>();
            if (tmp != null) return tmp.text != null && tmp.text.Contains(text);

            // Not an input field — nothing to verify against, so assume the events landed.
            return true;
        }

        private static void ClearField(GameObject field)
        {
            var legacy = field.GetComponentInChildren<InputField>();
            if (legacy != null) { legacy.text = string.Empty; return; }

            var tmp = field.GetComponentInChildren<TMPro.TMP_InputField>();
            if (tmp != null) tmp.text = string.Empty;
        }

        private static bool TryTypeIntoComponent(GameObject field, string text, bool clearFirst)
        {
            var legacy = field.GetComponentInChildren<InputField>();
            if (legacy != null)
            {
                legacy.ActivateInputField();
                if (clearFirst) legacy.text = string.Empty;
                foreach (var character in text)
                    legacy.ProcessEvent(new Event { type = EventType.KeyDown, character = character });
                legacy.ForceLabelUpdate();
                return true;
            }

            var tmp = field.GetComponentInChildren<TMPro.TMP_InputField>();
            if (tmp != null)
            {
                tmp.ActivateInputField();
                if (clearFirst) tmp.text = string.Empty;
                foreach (var character in text)
                    tmp.ProcessEvent(new Event { type = EventType.KeyDown, character = character });
                tmp.ForceLabelUpdate();
                return true;
            }

            return false;
        }
    }

    /// <summary>Presses and releases a key. Use <see cref="Repeat"/> for "tap jump three times".</summary>
    public sealed class PressKeyStep : TestStep
    {
        public string Key = "space";
        public float HoldSeconds = 0.05f;
        public int Repeat = 1;
        public float IntervalSeconds = 0.08f;

        public override string Describe() =>
            Repeat > 1 ? $"press {Key} ×{Repeat}" : $"press {Key}";

        public override IEnumerator Execute(TestContext ctx)
        {
            for (int i = 0; i < Mathf.Max(1, Repeat); i++)
            {
                yield return ctx.User.PressKey(Key, HoldSeconds);
                if (i + 1 < Repeat && IntervalSeconds > 0f)
                    yield return Wait.RealSeconds(IntervalSeconds);
            }
        }
    }

    /// <summary>Holds a key down (or releases it) so movement and modifiers can span other steps.</summary>
    public sealed class KeyHoldStep : TestStep
    {
        public string Key = "space";
        public bool Down = true;

        public override string Describe() => $"key {(Down ? "down" : "up")} {Key}";

        public override IEnumerator Execute(TestContext ctx) =>
            Down ? ctx.User.KeyDown(Key) : ctx.User.KeyUp(Key);
    }

    /// <summary>Presses a gamepad button.</summary>
    public sealed class GamepadButtonStep : TestStep
    {
        public string Button = "south";
        public float HoldSeconds = 0.08f;

        public override string Describe() => $"gamepad {Button}";

        public override IEnumerator Execute(TestContext ctx) => ctx.User.GamepadPress(Button, HoldSeconds);
    }

    /// <summary>Holds an analog stick in a direction for a while — walking, aiming, steering.</summary>
    public sealed class GamepadStickStep : TestStep
    {
        public string Stick = "left";
        public Vector2 Value = Vector2.up;
        public float Seconds = 0.5f;

        public override string Describe() => $"stick {Stick} {Value} for {Seconds:0.##}s";

        public override IEnumerator Execute(TestContext ctx) => ctx.User.GamepadStick(Stick, Value, Seconds);
    }
}
