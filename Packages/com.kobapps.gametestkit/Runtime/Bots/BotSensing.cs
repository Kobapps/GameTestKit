using System;
using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Everything a bot is allowed to know: the game's own state, and what is on screen.
    /// </summary>
    /// <remarks>
    /// Deliberately read-only. A bot senses through this and answers with a <see cref="BotAction"/>; there
    /// is no way from here to call a game action, load a scene or mutate anything. That is what keeps a bot
    /// honest — it is playing the game through its interface, not driving it from inside, so a flow it
    /// cannot complete is a flow a player cannot complete.
    /// <para>
    /// State comes from the same <see cref="GameTestBindings"/> that test expressions read, so a game
    /// exposes its state once and both scripted tests and bots see it. The screen comes from the same
    /// inspector the AI dump uses, so a bot can find a button it was never told about — which is what lets
    /// one bot survive a new popup appearing in a flow.
    /// </para>
    /// </remarks>
    public sealed class BotContext
    {
        private SceneSnapshot _screen;
        private int _screenFrame = -1;
        private readonly int _maxElements;

        public BotContext(System.Random random, int maxElements = 250)
        {
            Random = random ?? new System.Random();
            _maxElements = maxElements;
        }

        /// <summary>Seeded, so a bot's choices replay identically from the run's seed.</summary>
        public System.Random Random { get; }

        /// <summary>How many actions the bot has taken this run.</summary>
        public int ActionCount { get; internal set; }

        /// <summary>Seconds since the bot started playing.</summary>
        public float Elapsed => Time.realtimeSinceStartup - StartedAt;

        internal float StartedAt { get; set; }

        /// <summary>The action the driver performed last, and whether it changed anything.</summary>
        public BotAction LastAction { get; internal set; }

        /// <summary>False when the last action left every sensed value and the screen unchanged.</summary>
        public bool LastActionChangedSomething { get; internal set; } = true;

        /// <summary>How many actions in a row have changed nothing — a bot's own stuck signal.</summary>
        public int BarrenActions { get; internal set; }

        /// <summary>Errors the game logged during the run so far.</summary>
        public IReadOnlyList<string> LoggedErrors => Errors;

        internal readonly List<string> Errors = new List<string>();

        // ---------------------------------------------------------------- game state

        /// <summary>A bound value, or null when the game does not expose that path.</summary>
        public object Get(string path) =>
            GameTestBindings.TryGetValue(path, out var value) ? value : null;

        public bool Has(string path) => GameTestBindings.TryGetValue(path, out _);

        public string GetString(string path, string fallback = "") =>
            Get(path)?.ToString() ?? fallback;

        public bool GetBool(string path, bool fallback = false)
        {
            var value = Get(path);
            if (value is bool b) return b;
            return value != null && bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }

        public int GetInt(string path, int fallback = 0)
        {
            var value = Get(path);
            if (value is int i) return i;
            if (value is float f) return (int)f;
            if (value is double d) return (int)d;
            return value != null && int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }

        public float GetFloat(string path, float fallback = 0f)
        {
            var value = Get(path);
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is double d) return (float)d;
            return value != null &&
                   float.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        /// <summary>Evaluates a test expression, e.g. <c>"ms.pixelsAlive == 0"</c>.</summary>
        public bool Check(string expression)
        {
            try { return Expression.EvaluateBool(expression); }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- the screen

        /// <summary>What is on screen right now. Captured once per frame and reused.</summary>
        public SceneSnapshot Screen
        {
            get
            {
                if (_screenFrame != Time.frameCount)
                {
                    _screen = SceneInspector.Capture(_maxElements, includeHidden: false);
                    _screenFrame = Time.frameCount;
                }
                return _screen;
            }
        }

        public string ActiveScene => Screen.ActiveScene;

        /// <summary>Interactable elements a player could touch right now.</summary>
        public IReadOnlyList<ElementInfo> Interactables => Screen.Interactables;

        /// <summary>The first usable element whose label contains <paramref name="text"/>.</summary>
        public ElementInfo FindByText(string text)
        {
            foreach (var element in Screen.Interactables)
            {
                if (!element.Interactable || !element.Visible) continue;
                if (!string.IsNullOrEmpty(element.Text) &&
                    element.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    return element;
            }
            return null;
        }

        /// <summary>The first usable element whose recommended selector or path contains <paramref name="fragment"/>.</summary>
        public ElementInfo Find(string fragment)
        {
            foreach (var element in Screen.Interactables)
            {
                if (!element.Interactable || !element.Visible) continue;
                if ((element.Selector != null &&
                     element.Selector.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (element.Path != null &&
                     element.Path.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0))
                    return element;
            }
            return null;
        }

        /// <summary>Every usable element whose selector contains <paramref name="fragment"/>, in scene order.</summary>
        public List<ElementInfo> FindAll(string fragment)
        {
            var found = new List<ElementInfo>();
            foreach (var element in Screen.Interactables)
            {
                if (!element.Interactable || !element.Visible) continue;
                if (element.Selector != null &&
                    element.Selector.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(element);
            }
            return found;
        }

        /// <summary>A uniformly random pick, or null when the list is empty.</summary>
        public T Pick<T>(IReadOnlyList<T> items) where T : class =>
            items == null || items.Count == 0 ? null : items[Random.Next(items.Count)];

        /// <summary>True with probability <paramref name="chance"/> (0..1). For "does this bot use boosters".</summary>
        public bool Chance(float chance) => Random.NextDouble() < chance;

        /// <summary>Writes a line into the bot's run log, so a decision can explain itself.</summary>
        public void Log(string message) => Journal?.Invoke(message);

        internal Action<string> Journal;
    }
}
