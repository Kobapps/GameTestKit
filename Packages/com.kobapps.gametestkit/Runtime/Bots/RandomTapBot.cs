using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Taps whatever is on screen, at random. The bot that finds the flow bugs nobody scripted.
    /// </summary>
    /// <remarks>
    /// Every other bot goes where its author expected it to go, which means it only ever proves the paths
    /// someone already thought of. This one doesn't know what the game is, so it walks into the places a
    /// scripted test never visits — the back button on the third popup, the settings screen mid-level,
    /// the same button twice before the animation finishes. Those are where flow bugs live.
    /// <para>
    /// It ships with the framework because it needs nothing from the game: the scene inspector already
    /// reports what is interactable. The only configuration that matters is <see cref="_avoid"/> — the
    /// buttons a random walk must not press, because "quit" and "delete my save" end the session rather
    /// than exploring it.
    /// </para>
    /// </remarks>
    [CreateAssetMenu(menuName = "GameTestKit/Bots/Random Tapper", fileName = "RandomTapper")]
    public sealed class RandomTapBot : GameBot
    {
        [Header("Where not to go")]
        [Tooltip("Elements whose label or selector contains any of these are never touched.")]
        [SerializeField]
        private string[] _avoid = { "quit", "exit", "delete", "reset", "logout", "sign out", "purchase", "buy" };

        [Tooltip("Never tap anything whose label looks like a price. Store buttons are usually labelled " +
                 "'$4.99', not 'Buy', so a word list alone does not stop a random walk from spending money.")]
        [SerializeField] private bool _avoidsPrices = true;

        [Header("Behaviour")]
        [Tooltip("Chance of tapping the same element again instead of moving on — finds double-tap bugs.")]
        [Range(0f, 1f)] [SerializeField] private float _repeatsItself = 0.15f;

        [Tooltip("Chance of a random drag across the screen instead of a tap.")]
        [Range(0f, 1f)] [SerializeField] private float _dragsSometimes = 0.1f;

        [Tooltip("Chance of pressing Escape / back.")]
        [Range(0f, 1f)] [SerializeField] private float _pressesBack = 0.05f;

        [Tooltip("Chance of choosing among the elements it has tapped least this run, rather than " +
                 "uniformly. Keeps a random walk out of the corner it has already explored.")]
        [Range(0f, 1f)] [SerializeField] private float _prefersUntried = 0.8f;

        private string _lastSelector;
        private readonly Dictionary<string, int> _tapped = new Dictionary<string, int>();

        public override void OnRunStarted(BotContext ctx)
        {
            _lastSelector = null;
            _tapped.Clear();
        }

        public override IEnumerable<string> TrackedValues()
        {
            yield return "scene";
        }

        public override BotAction Decide(BotContext ctx)
        {
            if (ctx.Chance(_pressesBack))
                return BotAction.Press("escape", "back out of wherever this is");

            if (ctx.Chance(_dragsSometimes))
            {
                var from = new Vector2(
                    (float)ctx.Random.NextDouble() * UnityEngine.Screen.width,
                    (float)ctx.Random.NextDouble() * UnityEngine.Screen.height);
                var to = new Vector2(
                    (float)ctx.Random.NextDouble() * UnityEngine.Screen.width,
                    (float)ctx.Random.NextDouble() * UnityEngine.Screen.height);
                return BotAction.DragPoints(from, to, "random drag");
            }

            if (_lastSelector != null && ctx.Chance(_repeatsItself))
            {
                Count(_lastSelector);
                return BotAction.Tap(_lastSelector, "tap the same thing again");
            }

            var candidates = new List<ElementInfo>();
            foreach (var element in ctx.Interactables)
            {
                if (!element.Interactable || !element.Visible) continue;
                if (IsAvoided(element)) continue;
                candidates.Add(element);
            }

            if (candidates.Count == 0)
                return BotAction.Stuck("nothing interactable on screen");

            if (ctx.Chance(_prefersUntried)) KeepTheLeastTapped(candidates);

            var pick = candidates[ctx.Random.Next(candidates.Count)];
            Count(pick.Selector);
            _lastSelector = pick.Selector;

            var what = string.IsNullOrEmpty(pick.Text) ? pick.Selector : $"'{pick.Text}'";
            return BotAction.Tap(pick.Selector, $"tap {what}");
        }

        /// <summary>
        /// Narrows the candidates to the ones this run has touched least often.
        /// </summary>
        /// <remarks>
        /// Uniform random is what wedges a random walk in a two-room flow. Out of lives, the home screen
        /// often offers exactly one live button — the store — and the store offers exactly one that is
        /// not a price: close. A memoryless bot then opens and closes it until the budget runs out, and
        /// because the screen changes every beat nothing looks wrong. Preferring what it has tapped
        /// least makes the second visit to a room the least attractive move available, so the walk keeps
        /// spreading outwards; a genuinely closed flow still circles, and the driver reports that as the
        /// finding it is.
        /// </remarks>
        private void KeepTheLeastTapped(List<ElementInfo> candidates)
        {
            int fewest = int.MaxValue;
            foreach (var element in candidates) fewest = Mathf.Min(fewest, TapsOn(element.Selector));
            candidates.RemoveAll(element => TapsOn(element.Selector) > fewest);
        }

        private int TapsOn(string selector) =>
            !string.IsNullOrEmpty(selector) && _tapped.TryGetValue(selector, out var taps) ? taps : 0;

        private void Count(string selector)
        {
            if (string.IsNullOrEmpty(selector)) return;
            _tapped[selector] = TapsOn(selector) + 1;
        }

        private bool IsAvoided(ElementInfo element)
        {
            if (_avoidsPrices && LooksLikeAPrice(element.Text)) return true;

            foreach (var word in _avoid)
            {
                if (string.IsNullOrWhiteSpace(word)) continue;
                if (!string.IsNullOrEmpty(element.Text) &&
                    element.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (!string.IsNullOrEmpty(element.Selector) &&
                    element.Selector.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (!string.IsNullOrEmpty(element.Path) &&
                    element.Path.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// True for labels like "$4.99", "€1,99" or "12.99 USD".
        /// </summary>
        /// <remarks>
        /// Store buttons are almost never labelled "Buy" — they carry the localised price, so a word list
        /// does not stop a random walk from opening a purchase dialog. On a device signed into a real
        /// store account that is not a test artefact, it is a charge; and even in the Editor it parks a
        /// modal the bot then cannot dismiss, which burns the run and reports a dead end that is really
        /// just the bot having bought something.
        /// </remarks>
        private static bool LooksLikeAPrice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            foreach (var symbol in "$€£¥₩₪₹")
                if (text.IndexOf(symbol) >= 0) return true;

            foreach (var code in CurrencyCodes)
                if (text.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private static readonly string[] CurrencyCodes = { "USD", "EUR", "GBP", "JPY", "ILS", "INR" };
    }
}
