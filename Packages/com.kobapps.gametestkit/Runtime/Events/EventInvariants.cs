using System;
using System.Collections.Generic;
using System.Text;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// The session-wide checks — the defects no single event's payload can reveal.
    /// </summary>
    /// <remarks>
    /// Each rule catches a bug class that survives per-event assertions entirely:
    /// <list type="bullet">
    /// <item><description><b>Gaps.</b> A hole in the sequence is the only in-band evidence that an event
    /// was dropped between being emitted and being recorded.</description></item>
    /// <item><description><b>Pairing.</b> Two <c>Level_Start</c>s in a row with no end between them is a
    /// phantom attempt, and every attempt-grain metric silently doubles when it happens. Nothing about
    /// either event's payload is wrong.</description></item>
    /// <item><description><b>Duplicates.</b> The same event with an identical payload twice within a few
    /// hundred milliseconds is a double-subscribe, not a player doing something twice.</description></item>
    /// <item><description><b>Required properties.</b> A property every event is supposed to carry — a
    /// session id, a build number — is invisible to a test that only checks the one event it is
    /// about.</description></item>
    /// </list>
    /// Violations are returned rather than thrown, so the caller decides whether this run treats them as
    /// a failure, and so the whole set is reported at once instead of one per run.
    /// </remarks>
    public static class EventInvariants
    {
        /// <summary>How close two identical payloads must be to read as a double-send, in milliseconds.</summary>
        public const double DefaultDuplicateWindowMs = 250;

        /// <summary>What to check. Everything is opt-in except the checks that need no configuration.</summary>
        public sealed class Options
        {
            /// <summary>Report holes in the event sequence.</summary>
            public bool CheckGaps = true;

            /// <summary>
            /// A property carrying the game's <em>own</em> per-session event counter, e.g.
            /// <c>Event_Number</c>. Gaps are looked for in that when it is set.
            /// </summary>
            /// <remarks>
            /// This is the setting that makes the gap check worth running. Without it the check falls
            /// back to the log's own numbering, which is contiguous by construction — the log numbers
            /// what it <em>receives</em>, so an event the game emitted and the log never saw leaves no
            /// hole to find. A counter stamped by the game at emit time does leave one, and it is the
            /// only in-band evidence that something was dropped on the way.
            /// </remarks>
            public string SequenceProperty;

            /// <summary>Report identical payloads sent twice inside <see cref="DuplicateWindowMs"/>.</summary>
            public bool CheckDuplicates = true;

            public double DuplicateWindowMs = DefaultDuplicateWindowMs;

            /// <summary>
            /// Properties ignored when deciding whether two payloads are identical.
            /// <see cref="SequenceProperty"/> is always ignored.
            /// </summary>
            /// <remarks>
            /// Anything that differs by construction — a counter, a timestamp, a per-event id — would
            /// otherwise make every payload unique and quietly turn duplicate detection off.
            /// </remarks>
            public readonly List<string> IgnoreInDuplicates = new List<string>();

            /// <summary>Event names that must open and close in turn, e.g. <c>Level_Start</c> → <c>Level_End</c>.</summary>
            public readonly List<KeyValuePair<string, string>> Pairs = new List<KeyValuePair<string, string>>();

            /// <summary>Properties every event must carry. Absent or null on any event is a violation.</summary>
            public readonly List<string> RequiredProperties = new List<string>();

            /// <summary>Events exempt from <see cref="RequiredProperties"/> — usually the boot ones.</summary>
            public readonly List<string> RequiredPropertyExemptions = new List<string>();
        }

        /// <summary>Runs every enabled check over a window and returns what it found, in reading order.</summary>
        public static List<string> Run(IReadOnlyList<TestEvent> events, Options options = null)
        {
            var violations = new List<string>();
            options ??= new Options();

            if (events == null || events.Count == 0) return violations;

            if (options.CheckGaps) CheckGaps(events, options, violations);
            if (options.CheckDuplicates) CheckDuplicates(events, options, violations);

            foreach (var pair in options.Pairs) CheckPairing(events, pair.Key, pair.Value, violations);

            if (options.RequiredProperties.Count > 0) CheckRequiredProperties(events, options, violations);

            return violations;
        }

        private static void CheckGaps(IReadOnlyList<TestEvent> events, Options options,
            ICollection<string> violations)
        {
            long? previous = null;
            string previousName = null;

            foreach (var captured in events)
            {
                if (!TryNumber(captured, options.SequenceProperty, out var current))
                {
                    // An event without the counter cannot be placed in the run of numbers, so it is
                    // skipped rather than treated as a hole. Whether it should carry the property at
                    // all is what RequiredProperties is for.
                    continue;
                }

                if (previous != null && current != previous + 1)
                {
                    if (current <= previous)
                        violations.Add($"'{captured.Name}' is numbered {current}, which is not after " +
                                       $"'{previousName}' at {previous} — the sequence went backwards");
                    else
                        violations.Add($"{current - previous - 1} event(s) are missing between " +
                                       $"'{previousName}' at {previous} and '{captured.Name}' at {current}");
                }

                previous = current;
                previousName = captured.Name;
            }
        }

        /// <summary>The game's counter when one is configured, otherwise the log's own numbering.</summary>
        private static bool TryNumber(TestEvent captured, string property, out long number)
        {
            if (string.IsNullOrEmpty(property))
            {
                number = captured.Sequence;
                return true;
            }

            number = 0;
            if (!captured.TryGet(property, out var value) || value == null) return false;

            return long.TryParse(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out number);
        }

        private static void CheckDuplicates(IReadOnlyList<TestEvent> events, Options options,
            ICollection<string> violations)
        {
            var windowMs = options.DuplicateWindowMs;

            for (int i = 1; i < events.Count; i++)
            {
                var current = events[i];

                // Only backwards while still inside the window: an event repeated legitimately a minute
                // later is a player doing something twice, which is not what this rule is about.
                for (int j = i - 1; j >= 0; j--)
                {
                    var earlier = events[j];
                    var apart = (current.TimestampUtc - earlier.TimestampUtc).TotalMilliseconds;

                    if (apart > windowMs) break;
                    if (!string.Equals(earlier.Name, current.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!SamePayload(earlier, current, options)) continue;

                    violations.Add($"'{current.Name}' was sent twice with an identical payload " +
                                   $"{apart:0} ms apart (#{earlier.Sequence} and #{current.Sequence}) — " +
                                   "that reads as a double-send, not as two real occurrences");
                    break;
                }
            }
        }

        private static void CheckPairing(IReadOnlyList<TestEvent> events, string opening, string closing,
            ICollection<string> violations)
        {
            TestEvent open = null;

            foreach (var captured in events)
            {
                if (string.Equals(captured.Name, opening, StringComparison.OrdinalIgnoreCase))
                {
                    if (open != null)
                        violations.Add($"'{opening}' #{captured.Sequence} opened while #{open.Sequence} was " +
                                       $"still open — no '{closing}' between them");

                    open = captured;
                }
                else if (string.Equals(captured.Name, closing, StringComparison.OrdinalIgnoreCase))
                {
                    if (open == null)
                        violations.Add($"'{closing}' #{captured.Sequence} closed something that never opened " +
                                       $"— no '{opening}' before it");

                    open = null;
                }
            }
        }

        private static void CheckRequiredProperties(IReadOnlyList<TestEvent> events, Options options,
            ICollection<string> violations)
        {
            foreach (var captured in events)
            {
                if (IsExempt(captured.Name, options.RequiredPropertyExemptions)) continue;

                foreach (var key in options.RequiredProperties)
                {
                    if (captured.TryGet(key, out var value) && value != null) continue;

                    violations.Add($"'{captured.Name}' #{captured.Sequence} is missing the required " +
                                   $"property '{key}'");
                }
            }
        }

        private static bool IsExempt(string name, List<string> exemptions)
        {
            foreach (var exempt in exemptions)
                if (string.Equals(name, exempt, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static bool SamePayload(TestEvent left, TestEvent right, Options options)
        {
            if (left.Properties.Count != right.Properties.Count) return false;

            foreach (var pair in left.Properties)
            {
                if (Ignored(pair.Key, options)) continue;
                if (!right.TryGet(pair.Key, out var other)) return false;

                if (!string.Equals(EventPropertyMatcher.Describe(pair.Value),
                        EventPropertyMatcher.Describe(other), StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static bool Ignored(string key, Options options)
        {
            if (!string.IsNullOrEmpty(options.SequenceProperty) &&
                string.Equals(key, options.SequenceProperty, StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var ignored in options.IgnoreInDuplicates)
                if (string.Equals(key, ignored, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>The violations as one message a failing step can carry.</summary>
        public static string Summarize(IReadOnlyList<string> violations)
        {
            if (violations == null || violations.Count == 0) return "";

            var text = new StringBuilder();
            text.Append(violations.Count).Append(violations.Count == 1 ? " invariant broken:" : " invariants broken:");

            foreach (var violation in violations) text.Append(Environment.NewLine).Append("  • ").Append(violation);

            return text.ToString();
        }
    }
}
