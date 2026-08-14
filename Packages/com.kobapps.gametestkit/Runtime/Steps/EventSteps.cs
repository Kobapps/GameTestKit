using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Assertions about what the game <em>emitted</em>, as opposed to what it shows.
    /// </summary>
    /// <remarks>
    /// Analytics is the obvious use and the reason these exist, but the kit deliberately says "event"
    /// rather than "analytics": a server call, an IAP receipt, an ad callback, a remote-config fetch and
    /// a save write are the same shape, and one set of verbs covers all of them. The game decides what
    /// reaches <see cref="TestEventLog"/>.
    /// <para>
    /// The verbs are registered as ordinary steps, so they appear in the catalogue, the validator, the
    /// window and the generated AI skill with everything else.
    /// </para>
    /// </remarks>
    public static class EventSteps
    {
        public const string Category = "Events";

        /// <summary>Where a case's proof record is written, beside the test's own artifacts.</summary>
        public const string ProofFilePrefix = "event-proof";

        private const string IndexFirst = "first";
        private const string IndexLast = "last";

        /// <summary>The case a test currently has open. One at a time — a case is a window, not a stack.</summary>
        private static EventProofCase _openCase;

        /// <summary>Every case closed during this run, for the report.</summary>
        private static readonly List<EventProofCase> Closed = new List<EventProofCase>();

        /// <summary>The proofs gathered so far this run, oldest first.</summary>
        public static IReadOnlyList<EventProofCase> Proofs => Closed;

        /// <summary>Drops the run's cases. Called by the runner between runs, not by a test.</summary>
        internal static void ResetForRun()
        {
            _openCase = null;
            Closed.Clear();
        }

        /// <summary>
        /// Hands over the cases closed since the last call and forgets them, so the runner can file
        /// them against the test that produced them. A case left open when a test ends is closed here —
        /// a test that failed before its <c>eventProof</c> step still has evidence worth keeping.
        /// </summary>
        internal static List<EventProofCase> TakeClosed()
        {
            if (_openCase != null)
            {
                Closed.Add(_openCase);
                _openCase = null;
            }

            var taken = new List<EventProofCase>(Closed);
            Closed.Clear();
            return taken;
        }

        internal static void Register()
        {
            StepRegistry.Register(new StepDefinition
            {
                Key = "eventCase",
                Summary = "Opens a proof case: marks the event window and names the event being proved.",
                Category = Category,
                Parameters = new[]
                {
                    new StepParameter("eventCase", "string", "The event this case proves.", true),
                    new StepParameter("case", "string", "Which case of it — win, fail, first-install."),
                    new StepParameter("companions", "array",
                        "Other events allowed in the window. Anything else is flagged by expectOnlyExpectedEvents."),
                },
                Example = "{ \"eventCase\": \"Level_End\", \"case\": \"win\" }",
                Factory = json =>
                {
                    var step = new OpenCaseStep
                    {
                        Event = StepJson.RequiredString(json, "eventCase", "eventCase"),
                        Variant = StepJson.OptionalString(json, "case", ""),
                    };
                    foreach (var companion in json["companions"].AsStringList()) step.Companions.Add(companion);
                    return step;
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "expectEvent",
                Summary = "Waits for an event and asserts its payload, how many times it fired, and which sinks took it.",
                Category = Category,
                Parameters = new[]
                {
                    new StepParameter("expectEvent", "string", "The event name.", true),
                    new StepParameter("within", "number", "Seconds to wait for it.", false, "10"),
                    new StepParameter("count", "number", "Exactly how many must have fired in the window."),
                    new StepParameter("index", "string", "Which one to assert against: first or last.", false, "first"),
                    new StepParameter("props", "object",
                        "Property matchers: a literal, \"*\" present, \"!\" absent, \">=1\", \"~regex\", " +
                        "[\"a\",\"b\"], \"type:number\", \"@const:Type.PREFIX_*\"."),
                    new StepParameter("delivered", "array", "Sinks that must have received it."),
                    new StepParameter("notDelivered", "array", "Sinks that must NOT have received it."),
                    new StepParameter("screenshot", "bool", "Capture the frame the event went in.", false, "false"),
                },
                Example =
                    "{ \"expectEvent\": \"Level_End\", \"count\": 1, " +
                    "\"props\": { \"Result\": \"Win\", \"Duration\": \">0\" }, \"delivered\": [\"Mixpanel\"] }",
                Factory = json =>
                {
                    var step = new ExpectEventStep
                    {
                        Name = StepJson.RequiredString(json, "expectEvent", "expectEvent"),
                        Within = StepJson.Float(json, "within", 10f),
                        ExpectedCount = json.Has("count") ? json["count"].AsInt(1) : (int?)null,
                        Index = StepJson.OptionalString(json, "index", IndexFirst),
                        Properties = json.Has("props") ? json["props"] : null,
                        Screenshot = StepJson.Bool(json, "screenshot", false),
                    };
                    foreach (var sink in json["delivered"].AsStringList()) step.Delivered.Add(sink);
                    foreach (var sink in json["notDelivered"].AsStringList()) step.NotDelivered.Add(sink);
                    return step;
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "expectNoEvent",
                Summary = "Asserts an event did not fire in the window — the check that a legacy event stayed off.",
                Category = Category,
                Parameters = new[]
                {
                    new StepParameter("expectNoEvent", "string", "The event that must not have fired.", true),
                    new StepParameter("within", "number", "Seconds to keep watching before concluding it did not.",
                        false, "0"),
                },
                Example = "{ \"expectNoEvent\": \"legacy_level_end\" }",
                Factory = json => new ExpectNoEventStep
                {
                    Name = StepJson.RequiredString(json, "expectNoEvent", "expectNoEvent"),
                    Within = StepJson.Float(json, "within", 0f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "waitForEvent",
                Summary = "Waits until an event has fired. Replaces sleeping for long enough.",
                Category = Category,
                Parameters = new[]
                {
                    new StepParameter("waitForEvent", "string", "The event to wait for.", true),
                    new StepParameter("within", "number", "Seconds to wait.", false, "10"),
                    new StepParameter("count", "number", "How many to wait for.", false, "1"),
                },
                Example = "{ \"waitForEvent\": \"Session_Start\" }",
                Factory = json => new WaitForEventStep
                {
                    Name = StepJson.RequiredString(json, "waitForEvent", "waitForEvent"),
                    Within = StepJson.Float(json, "within", 10f),
                    Count = Math.Max(1, StepJson.Int(json, "count", 1)),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "expectOrder",
                Summary = "Asserts several events fired in this order, ignoring anything else between them.",
                Category = Category,
                Parameters = new[]
                {
                    new StepParameter("expectOrder", "array", "Event names in the order expected.", true),
                },
                Example = "{ \"expectOrder\": [\"Level_Start\", \"Level_End\"] }",
                Factory = json =>
                {
                    var step = new ExpectOrderStep();

                    foreach (var name in json["expectOrder"].AsStringList()) step.Events.Add(name);

                    if (step.Events.Count < 2)
                        throw new TestFailureException("expectOrder needs at least two event names.");

                    return step;
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "expectOnlyExpectedEvents",
                Summary = "Fails on any event in the window that the case did not name as its subject or a companion.",
                Category = Category,
                Parameters = new[] { new StepParameter("ignore", "array", "Extra names to tolerate.") },
                Example = "{ \"expectOnlyExpectedEvents\": true }",
                Factory = json =>
                {
                    var step = new ExpectOnlyExpectedStep();
                    foreach (var name in json["ignore"].AsStringList()) step.Ignore.Add(name);
                    return step;
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "eventInvariants",
                Summary = "Runs the session-wide sweep: sequence gaps, duplicate payloads, unbalanced pairs.",
                Category = Category,
                Parameters = new[]
                {
                    new StepParameter("pairs", "array",
                        "Opening/closing names as [\"Level_Start\",\"Level_End\"] pairs."),
                    new StepParameter("requiredProps", "array", "Properties every event must carry."),
                    new StepParameter("exempt", "array", "Events excused from requiredProps."),
                    new StepParameter("sequenceProperty", "string",
                        "The property carrying the game's own event counter. Gaps are looked for in " +
                        "that — the log's own numbering has no holes by construction."),
                    new StepParameter("ignoreInDuplicates", "array",
                        "Properties to ignore when comparing payloads, e.g. a timestamp."),
                    new StepParameter("duplicateWindowMs", "number",
                        "How close two identical payloads must be to read as a double-send.", false, "250"),
                    new StepParameter("gaps", "bool", "Report holes in the sequence.", false, "true"),
                },
                Example = "{ \"eventInvariants\": true, \"pairs\": [[\"Level_Start\", \"Level_End\"]] }",
                Factory = json =>
                {
                    var step = new EventInvariantsStep();

                    step.Options.CheckGaps = StepJson.Bool(json, "gaps", true);
                    step.Options.CheckDuplicates = StepJson.Bool(json, "duplicates", true);
                    step.Options.DuplicateWindowMs = StepJson.Float(json, "duplicateWindowMs",
                        (float)EventInvariants.DefaultDuplicateWindowMs);

                    foreach (var pair in json["pairs"])
                    {
                        if (!pair.IsArray) continue;

                        var names = pair.AsStringList();
                        if (names.Count == 2)
                            step.Options.Pairs.Add(new KeyValuePair<string, string>(names[0], names[1]));
                    }

                    step.Options.SequenceProperty = StepJson.OptionalString(json, "sequenceProperty", null);

                    foreach (var key in json["requiredProps"].AsStringList()) step.Options.RequiredProperties.Add(key);
                    foreach (var name in json["exempt"].AsStringList()) step.Options.RequiredPropertyExemptions.Add(name);
                    foreach (var key in json["ignoreInDuplicates"].AsStringList()) step.Options.IgnoreInDuplicates.Add(key);

                    return step;
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "eventProof",
                Summary = "Closes the case and writes its proof record beside the run's report.",
                Category = Category,
                Example = "{ \"eventProof\": true }",
                Factory = _ => new CloseCaseStep(),
            });
        }

        // ================================================================ steps

        private sealed class OpenCaseStep : TestStep
        {
            public string Event;
            public string Variant;
            public readonly List<string> Companions = new List<string>();

            public override string Describe() =>
                string.IsNullOrEmpty(Variant) ? $"open event case '{Event}'" : $"open event case '{Event}' ({Variant})";

            public override IEnumerator Execute(TestContext ctx)
            {
                if (!TestEventLog.Enabled)
                    ctx.Fail("TestEventLog is disabled, so no event could be observed. Set TestEventLog.Enabled " +
                             "or run a development build.");

                _openCase = new EventProofCase
                {
                    Subject = Event,
                    Variant = Variant,
                    TestName = ctx.Test?.Name,
                    Category = ctx.Test?.Category,
                    Mark = TestEventLog.Mark(),
                };

                _openCase.Companions.AddRange(Companions);
                yield break;
            }
        }

        private sealed class ExpectEventStep : TestStep
        {
            public string Name;
            public float Within = 10f;
            public int? ExpectedCount;
            public string Index = IndexFirst;
            public JsonValue Properties;
            public bool Screenshot;
            public readonly List<string> Delivered = new List<string>();
            public readonly List<string> NotDelivered = new List<string>();

            public override string Describe() => $"expect event '{Name}'";

            public override IEnumerator Execute(TestContext ctx)
            {
                var mark = MarkOf();
                int required = ExpectedCount ?? 1;

                var deadline = Time.realtimeSinceStartup + Mathf.Max(0f, Within);
                IReadOnlyList<TestEvent> matches;

                while (true)
                {
                    matches = TestEventLog.EntriesOf(Name, mark);
                    if (matches.Count >= required || Time.realtimeSinceStartup >= deadline) break;
                    yield return null;
                }

                if (matches.Count == 0)
                {
                    Record(EventVerdict.Missing,
                        $"no '{Name}' was recorded within {Within:0.#}s of the case opening");

                    // What DID happen is the whole diagnostic. "The event did not fire" sends someone
                    // back to the game to find out; this sends them to the right line.
                    ctx.Fail($"no '{Name}' was recorded within {Within:0.#}s. " +
                             $"What did fire: {TestEventLog.Describe(mark)}");
                }

                var subject = string.Equals(Index, IndexLast, StringComparison.OrdinalIgnoreCase)
                    ? matches[matches.Count - 1]
                    : matches[0];

                // The proof is the CASE'S event, not whatever the last expectation happened to match. A
                // case may legitimately assert on companions, and recording those over the proof would
                // leave the record naming one event while showing another's payload.
                bool isSubject = _openCase != null &&
                                 string.Equals(Name, _openCase.Subject, StringComparison.OrdinalIgnoreCase);

                if (isSubject) _openCase.Proof = subject;

                if (Screenshot && isSubject)
                {
                    yield return ctx.Screenshot(Name);

                    var artifacts = ctx.CurrentStep?.Artifacts;
                    if (artifacts != null && artifacts.Count > 0)
                        _openCase.ProofScreenshot = Path.GetFileName(artifacts[artifacts.Count - 1]);
                }

                var failures = new List<string>();
                CheckCount(matches, failures);
                CheckProperties(subject, failures);
                CheckRouting(subject, failures);

                if (failures.Count > 0)
                    ctx.Fail($"'{Name}' fired, but it did not say what was expected:" + Environment.NewLine +
                             string.Join(Environment.NewLine, failures));
            }

            private void CheckCount(IReadOnlyList<TestEvent> matches, ICollection<string> failures)
            {
                if (ExpectedCount == null || matches.Count == ExpectedCount.Value)
                {
                    if (ExpectedCount != null)
                        Record(EventVerdict.Proven, null, true, "expectEvent");
                    return;
                }

                var reason = $"expected {ExpectedCount.Value} '{Name}' event(s) in the window, saw {matches.Count}";
                Record(EventVerdict.CountMismatch, reason);
                failures.Add("  • " + reason);
            }

            private void CheckProperties(TestEvent subject, ICollection<string> failures)
            {
                if (Properties == null || !Properties.IsObject) return;

                var expectation = new EventExpectation("expectEvent", Name) { Passed = true };
                expectation.Properties.AddRange(EventPropertyMatcher.MatchAll(Properties, subject.Properties));

                foreach (var match in expectation.Properties)
                {
                    if (match.Passed) continue;

                    expectation.Passed = false;
                    expectation.Verdict = EventVerdict.PayloadMismatch;
                    failures.Add($"  • {match.Key}: expected {match.Expected}, got {match.Observed} — {match.Reason}");
                }

                if (!expectation.Passed) expectation.Reason = "the payload did not match";
                _openCase?.Record(expectation);
            }

            private void CheckRouting(TestEvent subject, ICollection<string> failures)
            {
                if (Delivered.Count == 0 && NotDelivered.Count == 0) return;

                var expectation = new EventExpectation("expectRouting", Name) { Passed = true };

                foreach (var sink in Delivered)
                {
                    if (subject.WasDeliveredTo(sink)) continue;

                    var delivery = subject.DeliveryTo(sink);
                    var why = delivery == null
                        ? "that sink never saw it"
                        : $"it was refused{(string.IsNullOrEmpty(delivery.Reason) ? "" : $": {delivery.Reason}")}";

                    expectation.Passed = false;
                    expectation.Verdict = EventVerdict.RoutingMismatch;
                    failures.Add($"  • '{Name}' should have reached {sink}, but {why}");
                }

                foreach (var sink in NotDelivered)
                {
                    if (!subject.WasDeliveredTo(sink)) continue;

                    expectation.Passed = false;
                    expectation.Verdict = EventVerdict.RoutingMismatch;
                    failures.Add($"  • '{Name}' must not reach {sink}, but it did");
                }

                if (!expectation.Passed) expectation.Reason = "the event went to the wrong places";
                _openCase?.Record(expectation);
            }

            private void Record(EventVerdict verdict, string reason, bool passed = false, string kind = "expectEvent")
            {
                _openCase?.Record(new EventExpectation(kind, Name)
                {
                    Passed = passed,
                    Reason = reason,
                    Verdict = verdict,
                });
            }
        }

        private sealed class ExpectNoEventStep : TestStep
        {
            public string Name;
            public float Within;

            public override string Describe() => $"expect no '{Name}'";

            public override IEnumerator Execute(TestContext ctx)
            {
                var mark = MarkOf();

                // Watching for a while before concluding absence: an event that fires 200 ms late is
                // still wrong, but a check that ran 200 ms early would have called it right.
                var deadline = Time.realtimeSinceStartup + Mathf.Max(0f, Within);
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (TestEventLog.CountOf(Name, mark) > 0) break;
                    yield return null;
                }

                var seen = TestEventLog.EntriesOf(Name, mark);
                bool passed = seen.Count == 0;

                _openCase?.Record(new EventExpectation("expectNoEvent", Name)
                {
                    Passed = passed,
                    Verdict = passed ? EventVerdict.Proven : EventVerdict.PayloadMismatch,
                    Reason = passed ? null : $"'{Name}' fired {seen.Count} time(s) and should not have",
                });

                if (!passed)
                    ctx.Fail($"'{Name}' must not fire here, but it fired {seen.Count} time(s) " +
                             $"(first at #{seen[0].Sequence}).");
            }
        }

        private sealed class WaitForEventStep : TestStep
        {
            public string Name;
            public float Within = 10f;
            public int Count = 1;

            public override string Describe() => $"wait for '{Name}'";

            public override IEnumerator Execute(TestContext ctx)
            {
                var mark = MarkOf();
                var deadline = Time.realtimeSinceStartup + Mathf.Max(0f, Within);

                while (TestEventLog.CountOf(Name, mark) < Count)
                {
                    if (Time.realtimeSinceStartup >= deadline)
                        ctx.Fail($"'{Name}' did not fire within {Within:0.#}s. " +
                                 $"What did fire: {TestEventLog.Describe(mark)}");

                    yield return null;
                }
            }
        }

        private sealed class ExpectOrderStep : TestStep
        {
            public readonly List<string> Events = new List<string>();

            public override string Describe() => $"expect order {string.Join(" → ", Events)}";

            public override IEnumerator Execute(TestContext ctx)
            {
                var window = TestEventLog.Since(MarkOf());

                int at = 0;
                var seen = new List<string>();

                foreach (var captured in window)
                {
                    if (at >= Events.Count) break;

                    if (!string.Equals(captured.Name, Events[at], StringComparison.OrdinalIgnoreCase)) continue;

                    seen.Add(captured.Name);
                    at++;
                }

                bool passed = at >= Events.Count;

                _openCase?.Record(new EventExpectation("expectOrder", Events.Count > 0 ? Events[0] : null)
                {
                    Passed = passed,
                    Verdict = passed ? EventVerdict.Proven : EventVerdict.OrderMismatch,
                    Reason = passed ? null : $"got as far as {(seen.Count == 0 ? "nothing" : string.Join(" → ", seen))}",
                });

                if (!passed)
                    ctx.Fail($"expected {string.Join(" → ", Events)}, but got as far as " +
                             $"{(seen.Count == 0 ? "nothing" : string.Join(" → ", seen))}. " +
                             $"The window held: {TestEventLog.Describe(MarkOf())}");

                yield break;
            }
        }

        private sealed class ExpectOnlyExpectedStep : TestStep
        {
            public readonly List<string> Ignore = new List<string>();

            public override string Describe() => "expect only the events this case declared";

            public override IEnumerator Execute(TestContext ctx)
            {
                if (_openCase == null)
                    ctx.Fail("expectOnlyExpectedEvents needs an open case — put an eventCase step before it.");

                var strays = new List<string>();

                foreach (var captured in TestEventLog.Since(_openCase.Mark))
                {
                    if (_openCase.IsExpected(captured.Name)) continue;
                    if (Ignore.Contains(captured.Name)) continue;
                    if (!strays.Contains(captured.Name)) strays.Add(captured.Name);
                }

                bool passed = strays.Count == 0;

                _openCase.Record(new EventExpectation("expectOnlyExpectedEvents", _openCase.Subject)
                {
                    Passed = passed,
                    Verdict = passed ? EventVerdict.Proven : EventVerdict.PayloadMismatch,
                    Reason = passed ? null : $"unexpected: {string.Join(", ", strays)}",
                });

                if (!passed)
                    ctx.Fail($"events fired that this case did not declare: {string.Join(", ", strays)}. " +
                             "Add them to \"companions\" if they belong here, or fix what sent them.");

                yield break;
            }
        }

        private sealed class EventInvariantsStep : TestStep
        {
            public readonly EventInvariants.Options Options = new EventInvariants.Options();

            public override string Describe() => "check event invariants";

            public override IEnumerator Execute(TestContext ctx)
            {
                var violations = EventInvariants.Run(TestEventLog.Entries, Options);
                bool passed = violations.Count == 0;

                _openCase?.Record(new EventExpectation("eventInvariants")
                {
                    Passed = passed,
                    Verdict = passed ? EventVerdict.Proven : EventVerdict.InvariantBroken,
                    Reason = passed ? null : EventInvariants.Summarize(violations),
                });

                if (!passed) ctx.Fail(EventInvariants.Summarize(violations));

                yield break;
            }
        }

        private sealed class CloseCaseStep : TestStep
        {
            public override string Describe() => "close the event case";

            public override IEnumerator Execute(TestContext ctx)
            {
                if (_openCase == null)
                {
                    ctx.Fail("eventProof needs an open case — put an eventCase step before it.");
                    yield break;
                }

                var closing = _openCase;
                _openCase = null;
                Closed.Add(closing);

                Write(ctx, closing);
                yield break;
            }

            /// <summary>
            /// Writes the record beside the test's own artifacts.
            /// </summary>
            /// <remarks>
            /// A file as well as the in-memory list, because the run happens in play mode and the Editor
            /// reads its results back from disk afterwards — an in-memory proof would not survive the
            /// domain reload that leaving play mode causes.
            /// </remarks>
            private static void Write(TestContext ctx, EventProofCase proof)
            {
                try
                {
                    var name = $"{ProofFilePrefix}-{Sanitize(proof.Subject)}" +
                               (string.IsNullOrEmpty(proof.Variant) ? "" : $"-{Sanitize(proof.Variant)}") + ".json";

                    var path = ctx.Artifacts.PathFor(name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, proof.ToJson().ToJson(), new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    // A proof that cannot be written must not turn a passing case red: the assertions
                    // already ran, and their verdict is the test's result.
                    Debug.LogWarning($"[GameTestKit] Could not write the event proof: {e.Message}");
                }
            }

            private static string Sanitize(string value)
            {
                if (string.IsNullOrEmpty(value)) return "case";

                var text = new StringBuilder(value.Length);
                foreach (var character in value)
                    text.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');

                return text.ToString().Trim('-');
            }
        }

        // ================================================================ helpers

        /// <summary>Where the current window starts — the open case's mark, or the whole session.</summary>
        private static long MarkOf() => _openCase?.Mark ?? 0;
    }
}
