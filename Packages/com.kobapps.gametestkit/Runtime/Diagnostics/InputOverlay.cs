using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Draws what the virtual user is doing on top of the running game: a yellow ring following the
    /// simulated pointer, a ripple wherever it taps, the path of a drag, and a caption strip carrying
    /// the current step and the text being typed, character by character.
    /// </summary>
    /// <remarks>
    /// Simulated input is invisible, and that is what makes a misbehaving test expensive to read: a run
    /// clicking two pixels outside a button looks exactly like a run clicking a dead button, and a run
    /// typing into the wrong field looks exactly like a field that ignores text. Showing the gesture
    /// turns both into something visible in a single play-through — and, because IMGUI is part of the
    /// presented frame, into something visible in the screenshot attached to the failure.
    /// <para>
    /// Drawing is IMGUI at a negative <see cref="GUI.depth"/>, which puts it above every camera and
    /// every canvas including Screen Space – Overlay, with no scene setup and no render-pipeline
    /// dependency. Nothing is created in batch mode or under <c>-nographics</c>, where there is no
    /// screen to draw on and no one to look at it.
    /// </para>
    /// </remarks>
    public static class InputOverlay
    {
        private const string PrefsKey = "GameTestKit.InputOverlay";
        private const int MaxLogLines = 5;
        private const float LogLineLife = 6f;
        private const float PointerLife = 4f;

        /// <summary>Colour of every marker. Yellow by default, because almost no game UI is.</summary>
        public static Color Tint = new Color(1f, 0.86f, 0.1f, 1f);

        /// <summary>
        /// Key that shows/hides the overlay while the game runs. <see cref="KeyCode.None"/> disables it.
        /// </summary>
        /// <remarks>
        /// Read from the real keyboard, so it does nothing during a run started with
        /// <see cref="RunOptions.IsolateRealDevices"/> — that option exists precisely to stop the
        /// hardware reaching the game. Use <see cref="Enabled"/> or the run option in that case.
        /// </remarks>
        public static KeyCode ToggleHotkey = KeyCode.F9;

        private static bool _enabled = true;
        private static bool _enabledLoaded;

        /// <summary>Whether the overlay draws at all. On by default; remembered once it is toggled.</summary>
        public static bool Enabled
        {
            get
            {
                if (!_enabledLoaded)
                {
                    _enabled = PlayerPrefs.GetInt(PrefsKey, 1) != 0;
                    _enabledLoaded = true;
                }
                return _enabled;
            }
            set
            {
                _enabled = value;
                _enabledLoaded = true;
                PlayerPrefs.SetInt(PrefsKey, value ? 1 : 0);
                if (!value) Clear();
            }
        }

        /// <summary>True when the overlay would draw right now — enabled, not suppressed, has a screen.</summary>
        public static bool IsShowing => Enabled && !_suppressed && CanDraw;

        public static void Toggle() => Enabled = !Enabled;

        // ---------------------------------------------------------------- session

        private static InputOverlayHost _host;
        private static bool _suppressed;
        private static bool _sessionActive;
        private static float _sessionStarted;
        private static string _sessionLabel;
        private static string _step;
        private static int _stepIndex = -1;

        /// <summary>
        /// Starts a run's overlay session. <paramref name="show"/> false suppresses the overlay for this
        /// run only, without touching the user's <see cref="Enabled"/> preference.
        /// </summary>
        public static void BeginSession(string label, bool show = true)
        {
            Clear();
            _suppressed = !show;
            _sessionActive = true;
            _sessionLabel = label;
            _sessionStarted = Now;
            _step = null;
            _stepIndex = -1;
            if (IsShowing) EnsureHost();
        }

        public static void EndSession()
        {
            _sessionActive = false;
            _suppressed = false;
            _step = null;
            _stepIndex = -1;
        }

        /// <summary>Sets the caption line — the step the runner is currently executing.</summary>
        public static void Step(string description, int index)
        {
            if (!IsShowing) return;
            _step = description;
            _stepIndex = index;
            EnsureHost();
        }

        /// <summary>Drops every marker and log line. Called when the overlay is switched off.</summary>
        public static void Clear()
        {
            _markers.Clear();
            _log.Clear();
            _typed.Length = 0;
            _typing = false;
            _pressed = false;
            _pointerAt = -999f;
            _typedAt = -999f;
        }

        // ---------------------------------------------------------------- reporting

        private static Vector2 _pointer;
        private static float _pointerAt = -999f;
        private static bool _pressed;
        private static Vector2 _pressPoint;

        private static readonly List<Marker> _markers = new List<Marker>();
        private static readonly List<LogLine> _log = new List<LogLine>();

        private static bool _typing;
        private static readonly StringBuilder _typed = new StringBuilder();
        private static float _typedAt = -999f;

        /// <summary>Moves the drawn pointer. Called on every interpolated step of a gesture.</summary>
        public static void Pointer(Vector2 screenPoint)
        {
            if (!IsShowing) return;
            _pointer = screenPoint;
            _pointerAt = Now;
            EnsureHost();
        }

        /// <summary>The pointer went down — a filled disc holds there until it comes back up.</summary>
        public static void Press(Vector2 screenPoint, PointerButton button = PointerButton.Left)
        {
            if (!IsShowing) return;
            Pointer(screenPoint);
            _pressed = true;
            _pressPoint = screenPoint;
        }

        /// <summary>The pointer came up — leaves a ripple, plus the drag path if it travelled.</summary>
        public static void Release(Vector2 screenPoint, PointerButton button = PointerButton.Left)
        {
            if (!IsShowing) return;
            Pointer(screenPoint);

            if (_pressed && (screenPoint - _pressPoint).sqrMagnitude > 64f)
                _markers.Add(Marker.Path(_pressPoint, screenPoint, Now));

            _pressed = false;
            _markers.Add(Marker.Ripple(screenPoint, Now));
        }

        /// <summary>A one-off ripple with no press behind it — scrolls, and custom steps that want one.</summary>
        public static void Ripple(Vector2 screenPoint)
        {
            if (!IsShowing) return;
            Pointer(screenPoint);
            _markers.Add(Marker.Ripple(screenPoint, Now));
        }

        /// <summary>Adds a line to the caption strip: "tap 640,360", "key space", "gamepad south".</summary>
        public static void Note(string text)
        {
            if (!IsShowing || string.IsNullOrEmpty(text)) return;

            _log.Add(new LogLine { Text = text, Born = Now });
            if (_log.Count > MaxLogLines * 2) _log.RemoveRange(0, _log.Count - MaxLogLines);
            EnsureHost();
        }

        /// <summary>Starts the live typing readout. Characters land through <see cref="Character"/>.</summary>
        public static void BeginText()
        {
            if (!IsShowing) return;
            _typed.Length = 0;
            _typing = true;
            _typedAt = Now;
            EnsureHost();
        }

        /// <summary>Appends one typed character to the readout, exactly as the game receives it.</summary>
        public static void Character(char character)
        {
            if (!IsShowing || !_typing) return;
            if (character == '\n') _typed.Append("\\n"); else _typed.Append(character);
            _typedAt = Now;
        }

        /// <summary>Ends the live readout; the finished string lingers a beat, then moves to the log.</summary>
        public static void EndText()
        {
            if (!_typing) return;
            _typing = false;
            _typedAt = Now;
            if (_typed.Length > 0) Note($"typed \"{Ellipsize(_typed.ToString(), 40)}\"");
        }

        /// <summary>Shows a whole string at once — for text delivered to a field directly, not keystroke by keystroke.</summary>
        public static void Text(string text)
        {
            if (!IsShowing || string.IsNullOrEmpty(text)) return;
            _typed.Length = 0;
            _typed.Append(text);
            _typing = false;
            _typedAt = Now;
            EnsureHost();
        }

        /// <summary>Formats a screen point the way the caption strip shows it.</summary>
        public static string Format(Vector2 screenPoint) => $"{screenPoint.x:0},{screenPoint.y:0}";

        // ---------------------------------------------------------------- host

        private static float Now => Time.realtimeSinceStartup;

        private static bool CanDraw =>
            !Application.isBatchMode &&
            SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static void EnsureHost()
        {
            if (_host != null || !CanDraw || !Application.isPlaying) return;

            var go = new GameObject("GameTestKit Input Overlay") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<InputOverlayHost>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Enter Play Mode Options can disable the domain reload, which would otherwise carry a
            // destroyed host, a previous run's markers and orphaned textures into the next session.
            _host = null;
            _suppressed = false;
            _sessionActive = false;
            _sessionLabel = null;

            Discard(ref _ring);
            Discard(ref _disc);
            Discard(ref _pixel);
            _styles = null;

            Clear();
        }

        internal static void PollHotkey()
        {
            if (ToggleHotkey != KeyCode.None && WasPressed(ToggleHotkey)) Toggle();
        }

        private static bool WasPressed(KeyCode code)
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null &&
                Enum.TryParse<UnityEngine.InputSystem.Key>(code.ToString(), true, out var key) &&
                key != UnityEngine.InputSystem.Key.None)
            {
                var control = keyboard[key];
                if (control != null && control.wasPressedThisFrame) return true;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(code)) return true;
#endif
            return false;
        }

        // ---------------------------------------------------------------- drawing

        private static Texture2D _ring, _disc, _pixel;
        private static Styles _styles;

        internal static void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!Enabled || _suppressed) return;

            // Lower depth draws later, so this lands on top of every other OnGUI in the project.
            GUI.depth = -1000;

            float now = Now;
            Prune(now);

            float scale = Mathf.Clamp(Screen.height / 720f, 0.8f, 2.2f);
            var previousColor = GUI.color;

            DrawMarkers(now, scale);
            DrawPointer(now, scale);
            DrawPanel(now, scale);

            GUI.color = previousColor;
        }

        private static void Prune(float now)
        {
            for (int i = _markers.Count - 1; i >= 0; i--)
                if (now - _markers[i].Born > _markers[i].Life)
                    _markers.RemoveAt(i);

            for (int i = _log.Count - 1; i >= 0; i--)
                if (now - _log[i].Born > LogLineLife)
                    _log.RemoveAt(i);
        }

        private static void DrawMarkers(float now, float scale)
        {
            foreach (var marker in _markers)
            {
                float t = Mathf.Clamp01((now - marker.Born) / marker.Life);

                if (marker.IsPath)
                {
                    float alpha = 1f - t;
                    DrawLine(marker.From, marker.To, 2.5f * scale, Fade(alpha * 0.7f));
                    DrawCircle(marker.From, 9f * scale, Fade(alpha * 0.8f), Ring);
                    DrawCircle(marker.To, 13f * scale, Fade(alpha), Ring);
                }
                else
                {
                    // Expanding ring: reads as "something happened here" even at a glance, and the
                    // size makes it survive a screenshot scaled down into a report.
                    float radius = Mathf.Lerp(10f, 48f, t) * scale;
                    DrawCircle(marker.From, radius, Fade(1f - t), Ring);
                    DrawCircle(marker.From, 5f * scale, Fade((1f - t) * 0.9f), Disc);
                }
            }
        }

        private static void DrawPointer(float now, float scale)
        {
            float age = now - _pointerAt;
            if (age > PointerLife) return;

            float alpha = Mathf.Clamp01(1f - (age - PointerLife * 0.5f) / (PointerLife * 0.5f));

            if (_pressed)
            {
                float pulse = 0.85f + 0.15f * Mathf.Sin(now * 12f);
                DrawCircle(_pressPoint, 20f * scale * pulse, Fade(alpha * 0.35f), Disc);
                DrawCircle(_pressPoint, 20f * scale * pulse, Fade(alpha), Ring);

                if ((_pointer - _pressPoint).sqrMagnitude > 64f)
                    DrawLine(_pressPoint, _pointer, 2.5f * scale, Fade(alpha * 0.8f));
            }

            DrawCircle(_pointer, 13f * scale, Fade(alpha), Ring);
            DrawLine(_pointer + new Vector2(-6f * scale, 0f), _pointer + new Vector2(6f * scale, 0f),
                1.5f * scale, Fade(alpha * 0.9f));
            DrawLine(_pointer + new Vector2(0f, -6f * scale), _pointer + new Vector2(0f, 6f * scale),
                1.5f * scale, Fade(alpha * 0.9f));
        }

        private static void DrawPanel(float now, float scale)
        {
            var styles = Styles.For(scale);
            var lines = new List<Line>();

            if (_sessionActive && !string.IsNullOrEmpty(_sessionLabel))
                lines.Add(new Line($"GameTestKit - {_sessionLabel}", styles.Muted));

            if (!string.IsNullOrEmpty(_step))
                lines.Add(new Line($"{(_stepIndex >= 0 ? _stepIndex + 1 + "  " : "")}{_step}", styles.Step));

            bool showTyped = _typed.Length > 0 && (_typing || now - _typedAt < 2.5f);
            if (showTyped)
            {
                // The caret blinks only while characters are still arriving, so a finished string does
                // not look like it is still being typed.
                string caret = _typing && Mathf.Repeat(now, 0.9f) < 0.55f ? "|" : " ";
                lines.Add(new Line($"\"{Ellipsize(_typed.ToString(), 48)}\"{caret}", styles.Typed));
            }

            int first = Mathf.Max(0, _log.Count - MaxLogLines);
            for (int i = first; i < _log.Count; i++)
                lines.Add(new Line("- " + _log[i].Text, styles.Muted));

            if (_sessionActive && ToggleHotkey != KeyCode.None && now - _sessionStarted < 8f)
                lines.Add(new Line($"{ToggleHotkey} hides this overlay", styles.Hint));

            if (lines.Count == 0) return;

            float pad = 10f * scale;
            float width = 0f, height = 0f;
            foreach (var line in lines)
            {
                var size = line.Style.CalcSize(new GUIContent(line.Text));
                width = Mathf.Max(width, size.x);
                height += size.y + 2f * scale;
            }

            width = Mathf.Min(width + pad * 2f, Screen.width - 24f * scale);
            var panel = new Rect(12f * scale, Screen.height - height - pad * 2f - 12f * scale,
                width, height + pad * 2f);

            GUI.color = new Color(0f, 0f, 0f, 0.66f);
            GUI.DrawTexture(panel, Pixel);
            GUI.color = new Color(Tint.r, Tint.g, Tint.b, 0.9f);
            GUI.DrawTexture(new Rect(panel.x, panel.y, 3f * scale, panel.height), Pixel);
            GUI.color = Color.white;

            float y = panel.y + pad;
            foreach (var line in lines)
            {
                float lineHeight = line.Style.CalcSize(new GUIContent(line.Text)).y;
                var rect = new Rect(panel.x + pad + 4f * scale, y, panel.width - pad * 2f, lineHeight);

                var shadow = rect;
                shadow.position += new Vector2(1f, 1f);
                var tint = line.Style.normal.textColor;
                line.Style.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
                GUI.Label(shadow, line.Text, line.Style);
                line.Style.normal.textColor = tint;
                GUI.Label(rect, line.Text, line.Style);

                y += lineHeight + 2f * scale;
            }
        }

        // ---------------------------------------------------------------- primitives

        private static Color Fade(float alpha) => new Color(Tint.r, Tint.g, Tint.b, Mathf.Clamp01(alpha) * Tint.a);

        /// <summary>Draws a texture centred on a screen point, flipping y into GUI space.</summary>
        private static void DrawCircle(Vector2 screenPoint, float radius, Color color, Texture2D texture)
        {
            if (radius <= 0f || color.a <= 0.01f) return;

            GUI.color = color;
            GUI.DrawTexture(new Rect(screenPoint.x - radius, Screen.height - screenPoint.y - radius,
                radius * 2f, radius * 2f), texture);
            GUI.color = Color.white;
        }

        private static void DrawLine(Vector2 from, Vector2 to, float thickness, Color color)
        {
            if (color.a <= 0.01f) return;

            var start = new Vector2(from.x, Screen.height - from.y);
            var end = new Vector2(to.x, Screen.height - to.y);
            var delta = end - start;
            float length = delta.magnitude;
            if (length < 0.5f) return;

            var matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);

            GUI.color = color;
            GUI.DrawTexture(new Rect(start.x, start.y - thickness * 0.5f, length, thickness), Pixel);
            GUI.color = Color.white;

            GUI.matrix = matrix;
        }

        private static Texture2D Pixel => _pixel != null ? _pixel : (_pixel = Solid());
        private static Texture2D Ring => _ring != null ? _ring : (_ring = Circle(128, 0.72f));
        private static Texture2D Disc => _disc != null ? _disc : (_disc = Circle(128, 0f));

        private static Texture2D Solid()
        {
            var texture = New(1, 1);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// A white circle in the alpha channel: a filled disc when <paramref name="inner"/> is 0, an
        /// annulus otherwise. Both edges are feathered over a pixel or so, which is what keeps the ring
        /// from looking like a staircase once it is scaled up to ripple size.
        /// </summary>
        private static Texture2D Circle(int size, float inner)
        {
            var texture = New(size, size);
            var pixels = new Color32[size * size];

            float radius = size * 0.5f;
            float feather = 1.5f / radius;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - radius) / radius;
                    float dy = (y + 0.5f - radius) / radius;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    float alpha = Mathf.Clamp01((1f - distance) / feather);
                    if (inner > 0f) alpha = Mathf.Min(alpha, Mathf.Clamp01((distance - inner) / feather));

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        private static void Discard(ref Texture2D texture)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        private static Texture2D New(int width, int height)
        {
            return new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        private static string Ellipsize(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return "..." + text.Substring(text.Length - max + 3);
        }

        // ---------------------------------------------------------------- small types

        private struct Marker
        {
            public Vector2 From, To;
            public float Born, Life;
            public bool IsPath;

            public static Marker Ripple(Vector2 at, float now) =>
                new Marker { From = at, To = at, Born = now, Life = 0.55f };

            public static Marker Path(Vector2 from, Vector2 to, float now) =>
                new Marker { From = from, To = to, Born = now, Life = 0.9f, IsPath = true };
        }

        private struct LogLine
        {
            public string Text;
            public float Born;
        }

        private readonly struct Line
        {
            public readonly string Text;
            public readonly GUIStyle Style;

            public Line(string text, GUIStyle style)
            {
                Text = text;
                Style = style;
            }
        }

        /// <summary>Styles are rebuilt only when the screen size changes them, not every repaint.</summary>
        private sealed class Styles
        {
            public GUIStyle Step, Typed, Muted, Hint;
            private float _scale;

            public static Styles For(float scale)
            {
                if (_styles != null && Mathf.Approximately(_styles._scale, scale)) return _styles;

                int body = Mathf.RoundToInt(13f * scale);
                _styles = new Styles
                {
                    _scale = scale,
                    Step = Build(Mathf.RoundToInt(15f * scale), Color.white, FontStyle.Bold),
                    Typed = Build(Mathf.RoundToInt(17f * scale), Tint, FontStyle.Bold),
                    Muted = Build(body, new Color(0.85f, 0.85f, 0.85f, 0.9f), FontStyle.Normal),
                    Hint = Build(Mathf.RoundToInt(11f * scale), new Color(0.7f, 0.7f, 0.7f, 0.8f), FontStyle.Italic),
                };
                return _styles;
            }

            private static GUIStyle Build(int fontSize, Color color, FontStyle fontStyle)
            {
                var style = new GUIStyle
                {
                    fontSize = fontSize,
                    fontStyle = fontStyle,
                    alignment = TextAnchor.MiddleLeft,
                    richText = false,
                    wordWrap = false,
                };
                style.normal.textColor = color;
                return style;
            }
        }
    }

    /// <summary>
    /// The one <c>MonoBehaviour</c> behind <see cref="InputOverlay"/>: it exists to own an
    /// <c>OnGUI</c> and an <c>Update</c>, and nothing else. Hidden and undestroyed between scenes, so a
    /// test that loads a scene mid-run does not lose its overlay.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class InputOverlayHost : MonoBehaviour
    {
        private void Update() => InputOverlay.PollHotkey();

        private void OnGUI() => InputOverlay.Draw();
    }
}
