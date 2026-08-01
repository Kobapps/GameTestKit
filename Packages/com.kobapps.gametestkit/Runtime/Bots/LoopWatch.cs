using System.Collections.Generic;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Watches for a bot going round in circles — the same handful of screens, over and over.
    /// </summary>
    /// <remarks>
    /// The dead-end detector counts beats that changed nothing, and a circle changes something every
    /// beat: opening the store is a new screen, closing it is a new screen again. So a bot that can only
    /// open and close the store looks like it is making progress right up to the end of its budget, and
    /// the run reports "budget spent" for what is really a wedged flow — typically a game state, out of
    /// lives being the usual one, that leaves nothing else to press.
    /// <para>
    /// The rule is about states rather than actions, because it is the screen coming back that matters,
    /// not which button produced it. Tracked bindings are part of the state fingerprint, so a bot
    /// circling two screens while a counter climbs is not looping — it is grinding, and that is a
    /// legitimate way to play.
    /// </para>
    /// </remarks>
    internal sealed class LoopWatch
    {
        private readonly List<string> _recent = new List<string>();
        private readonly int _window;
        private readonly int _maxStates;
        private readonly int _visits;

        internal LoopWatch(int window, int maxStates, int visitsBeforeFinding)
        {
            _window = window;
            _maxStates = maxStates;
            _visits = visitsBeforeFinding;
        }

        /// <summary>False when the run has switched loop detection off.</summary>
        internal bool Enabled => _visits > 1 && _window > 1 && _maxStates > 1;

        /// <summary>Notes the state the game settled into after a beat.</summary>
        internal void Record(string fingerprint)
        {
            if (!Enabled) return;
            _recent.Add(fingerprint);
            if (_recent.Count > _window) _recent.RemoveAt(0);
        }

        /// <summary>Forgets the history — after a change of scene, or once a circle has been reported.</summary>
        internal void Forget() => _recent.Clear();

        /// <summary>
        /// True when the recent window holds only a few states and the current one keeps returning.
        /// </summary>
        /// <remarks>
        /// Two states alternating is the classic (open the store, close the store) and three covers a
        /// triangle; past that the bot is genuinely wandering, even if it does revisit screens. A window
        /// of one state is deliberately not a circle — that is standing still, which the dead-end
        /// detector already reports, and in better words.
        /// </remarks>
        internal bool IsGoingInCircles(out int distinctStates, out int visits)
        {
            distinctStates = 0;
            visits = 0;
            if (!Enabled || _recent.Count < _window) return false;

            var counts = new Dictionary<string, int>();
            foreach (var state in _recent)
            {
                counts.TryGetValue(state, out var seen);
                counts[state] = seen + 1;
            }

            distinctStates = counts.Count;
            visits = counts[_recent[_recent.Count - 1]];

            return distinctStates > 1 && distinctStates <= _maxStates && visits >= _visits;
        }
    }
}
