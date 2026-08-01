using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Finds a bot by name, so a script can say <c>{"runBot": "Casual"}</c>.
    /// </summary>
    /// <remarks>
    /// Bot assets live in <c>Resources/GameBots/</c> so they are available in a built player too — a bot
    /// that only exists in the Editor cannot soak-test a device build, which is where the interesting
    /// input bugs are. Code-only bots can register themselves from a bootstrap for cases where a persona
    /// is genuinely logic rather than configuration.
    /// </remarks>
    public static class BotRegistry
    {
        /// <summary>Resources sub-folder scanned for <see cref="GameBot"/> assets.</summary>
        public const string ResourcesFolder = "GameBots";

        private static readonly Dictionary<string, GameBot> Extra =
            new Dictionary<string, GameBot>(StringComparer.OrdinalIgnoreCase);

        private static GameBot[] _loaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCache()
        {
            // Survives play-mode exit when the domain reload is disabled, and would otherwise hand out
            // bots belonging to a dead session.
            _loaded = null;
            Extra.Clear();
        }

        /// <summary>Registers a bot built in code rather than authored as an asset.</summary>
        public static void Register(GameBot bot)
        {
            if (bot == null) throw new ArgumentNullException(nameof(bot));
            Extra[bot.BotName] = bot;
        }

        /// <summary>Every bot available, assets first.</summary>
        public static List<GameBot> All()
        {
            _loaded ??= Resources.LoadAll<GameBot>(ResourcesFolder);

            var all = new List<GameBot>(_loaded);
            foreach (var bot in Extra.Values)
                if (!all.Contains(bot)) all.Add(bot);

            all.Sort((a, b) => string.CompareOrdinal(a.BotName, b.BotName));
            return all;
        }

        /// <summary>The bot with that name, or null.</summary>
        public static GameBot Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (Extra.TryGetValue(name, out var registered)) return registered;

            foreach (var bot in All())
                if (string.Equals(bot.BotName, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bot.name, name, StringComparison.OrdinalIgnoreCase))
                    return bot;

            return null;
        }

        /// <summary>The bot with that name, or a failure naming the ones that do exist.</summary>
        public static GameBot Require(string name)
        {
            var bot = Find(name);
            if (bot != null) return bot;

            var available = All();
            var names = new List<string>();
            foreach (var candidate in available) names.Add(candidate.BotName);

            throw new TestFailureException(
                $"No bot named '{name}'. " +
                (names.Count == 0
                    ? $"No GameBot assets were found — create one and put it in Resources/{ResourcesFolder}/."
                    : "Available: " + string.Join(", ", names) + "."));
        }
    }
}
