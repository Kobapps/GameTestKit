using System;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    public enum PointerButton
    {
        Left = 0,
        Right = 1,
        Middle = 2,
    }

    [Flags]
    public enum InputCapability
    {
        None = 0,
        Pointer = 1 << 0,
        Scroll = 1 << 1,
        Keys = 1 << 2,
        Text = 1 << 3,
        Gamepad = 1 << 4,
        Touch = 1 << 5,
    }

    /// <summary>
    /// Low-level input injection. Implementations put events into the same pipeline the player's
    /// hardware uses, so the game cannot tell the difference between a test and a human.
    /// </summary>
    /// <remarks>
    /// Backends are stateful: <see cref="Begin"/> creates virtual devices, <see cref="End"/> removes
    /// them and releases anything still held. All coordinates are screen pixels with the origin at the
    /// bottom-left, matching <c>UnityEngine.Screen</c> and <c>Input.mousePosition</c>.
    /// </remarks>
    public interface IInputBackend : IDisposable
    {
        string Name { get; }

        InputCapability Capabilities { get; }

        /// <summary>Current virtual pointer position in screen pixels.</summary>
        Vector2 PointerPosition { get; }

        void Begin(RunOptions options);

        /// <summary>Releases held buttons/keys and tears down virtual devices.</summary>
        void End();

        void PointerMove(Vector2 screenPosition);

        void PointerDown(Vector2 screenPosition, PointerButton button);

        void PointerUp(Vector2 screenPosition, PointerButton button);

        void Scroll(Vector2 screenPosition, Vector2 delta);

        /// <summary>Presses a key by name (see <see cref="KeyNames"/>), e.g. <c>space</c>, <c>a</c>, <c>escape</c>.</summary>
        void KeyDown(string key);

        void KeyUp(string key);

        /// <summary>Delivers a text character the way an OS IME would — what input fields actually read.</summary>
        void TextInput(char character);

        void GamepadButton(string button, bool pressed);

        void GamepadStick(string stick, Vector2 value);

        /// <summary>Pushes queued events into the engine. The runner also yields a frame after this.</summary>
        void Flush();
    }

    /// <summary>Backend-agnostic key naming, so scripts stay portable.</summary>
    public static class KeyNames
    {
        /// <summary>Normalises <c>Space</c>, <c>SPACE</c>, <c>spacebar</c> … to a canonical lowercase name.</summary>
        public static string Normalize(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            key = key.Trim().ToLowerInvariant();
            switch (key)
            {
                case " ":
                case "spacebar": return "space";
                case "return": return "enter";
                case "esc": return "escape";
                case "del": return "delete";
                case "ins": return "insert";
                case "ctrl": return "leftctrl";
                case "control": return "leftctrl";
                case "shift": return "leftshift";
                case "alt": return "leftalt";
                case "cmd":
                case "command":
                case "meta": return "leftmeta";
                case "up": return "uparrow";
                case "down": return "downarrow";
                case "left": return "leftarrow";
                case "right": return "rightarrow";
                case "pgup": return "pageup";
                case "pgdn": return "pagedown";
                default: return key;
            }
        }
    }
}
