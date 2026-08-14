using System;
using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;

namespace Kobapps.GameTestKit
{
    /// <summary>How an expectation came out, in the terms a report groups by.</summary>
    public enum EventVerdict
    {
        /// <summary>Everything the case asked for was true.</summary>
        Proven = 0,

        /// <summary>The event never fired in the window.</summary>
        Missing = 1,

        /// <summary>It fired, but the payload did not say what was expected.</summary>
        PayloadMismatch = 2,

        /// <summary>The payload was right; a sink did not get it, or got one it should not have.</summary>
        RoutingMismatch = 3,

        /// <summary>It fired the wrong number of times.</summary>
        CountMismatch = 4,

        /// <summary>Events fired in the wrong order.</summary>
        OrderMismatch = 5,

        /// <summary>A session-wide rule was broken.</summary>
        InvariantBroken = 6,
    }

    /// <summary>One assertion a case made, and what it found.</summary>
    public sealed class EventExpectation
    {
        /// <summary>The step verb that made it — <c>expectEvent</c>, <c>expectOrder</c>, …</summary>
        public string Kind;

        /// <summary>The event it was about, when it was about one.</summary>
        public string Event;

        public bool Passed;
        public string Reason;
        public EventVerdict Verdict = EventVerdict.Proven;

        /// <summary>Per-property verdicts, for the expectations that check a payload.</summary>
        public readonly List<EventPropertyMatch> Properties = new List<EventPropertyMatch>();

        public EventExpectation(string kind, string eventName = null)
        {
            Kind = kind;
            Event = eventName;
        }
    }

    /// <summary>
    /// One proof: the game was played into a state, one event was expected, and this is what the wire
    /// actually carried.
    /// </summary>
    /// <remarks>
    /// A case exists so a run produces evidence rather than a pass mark. "The event fired" is the least
    /// interesting thing a test can say about telemetry — what a reviewer needs is the payload, which
    /// sinks took it, and the frame of the game at the moment it went. That is what gets written beside
    /// the report and rendered into it.
    /// </remarks>
    public sealed class EventProofCase
    {
        /// <summary>The event this case is about. Companions may be asserted too, but this is the subject.</summary>
        public string Subject;

        /// <summary>Which case of the subject this is — <c>win</c>, <c>fail</c>, <c>first-install</c>.</summary>
        public string Variant;

        /// <summary>The test that opened it.</summary>
        public string TestName;

        public string Category;

        /// <summary>Where the window starts: events at or below this sequence are from before the case.</summary>
        public long Mark;

        /// <summary>Events the case tolerates in its window without them being the subject.</summary>
        public readonly List<string> Companions = new List<string>();

        public readonly List<EventExpectation> Expectations = new List<EventExpectation>();

        /// <summary>The subject event as it actually went, once it has been seen.</summary>
        public TestEvent Proof;

        /// <summary>File name of the screenshot taken at the moment of the event, if any.</summary>
        public string ProofScreenshot;

        public bool Passed
        {
            get
            {
                foreach (var expectation in Expectations)
                    if (!expectation.Passed) return false;

                return true;
            }
        }

        /// <summary>The verdict a report headlines the case with — the first thing that went wrong.</summary>
        public EventVerdict Verdict
        {
            get
            {
                foreach (var expectation in Expectations)
                    if (!expectation.Passed) return expectation.Verdict;

                return EventVerdict.Proven;
            }
        }

        public EventExpectation Record(EventExpectation expectation)
        {
            Expectations.Add(expectation);
            return expectation;
        }

        /// <summary>True when an event is the subject or one the case declared it expects to see.</summary>
        public bool IsExpected(string eventName)
        {
            if (string.Equals(eventName, Subject, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var companion in Companions)
                if (string.Equals(eventName, companion, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        // ---------------------------------------------------------------- serialisation

        /// <summary>The record written beside the run report and rendered into it.</summary>
        public JsonValue ToJson()
        {
            var root = JsonValue.NewObject()
                .Set("subject", Subject ?? "")
                .Set("variant", Variant ?? "")
                .Set("test", TestName ?? "")
                .Set("category", Category ?? "")
                .Set("passed", Passed)
                .Set("verdict", Verdict.ToString());

            if (!string.IsNullOrEmpty(ProofScreenshot)) root.Set("screenshot", ProofScreenshot);

            if (Proof != null)
            {
                var proof = JsonValue.NewObject()
                    .Set("name", Proof.Name)
                    .Set("sequence", Proof.Sequence)
                    .Set("atSeconds", Proof.AtRealtime)
                    .Set("suppressed", Proof.Suppressed);

                var props = JsonValue.NewObject();
                foreach (var pair in Proof.Properties)
                    props.Set(pair.Key, EventPropertyMatcher.Describe(pair.Value));
                proof.Set("properties", props);

                var sinks = JsonValue.NewArray();
                foreach (var delivery in Proof.Deliveries)
                    sinks.Add(JsonValue.NewObject()
                        .Set("sink", delivery.Sink)
                        .Set("delivered", delivery.Delivered)
                        .Set("reason", delivery.Reason ?? ""));
                proof.Set("deliveries", sinks);

                root.Set("event", proof);
            }

            var expectations = JsonValue.NewArray();
            foreach (var expectation in Expectations)
            {
                var item = JsonValue.NewObject()
                    .Set("kind", expectation.Kind ?? "")
                    .Set("event", expectation.Event ?? "")
                    .Set("passed", expectation.Passed)
                    .Set("verdict", expectation.Verdict.ToString());

                if (!string.IsNullOrEmpty(expectation.Reason)) item.Set("reason", expectation.Reason);

                if (expectation.Properties.Count > 0)
                {
                    var properties = JsonValue.NewArray();
                    foreach (var match in expectation.Properties)
                    {
                        var row = JsonValue.NewObject()
                            .Set("key", match.Key ?? "")
                            .Set("expected", match.Expected ?? "")
                            .Set("observed", match.Observed ?? "")
                            .Set("passed", match.Passed);

                        if (!string.IsNullOrEmpty(match.Reason)) row.Set("reason", match.Reason);
                        properties.Add(row);
                    }
                    item.Set("properties", properties);
                }

                expectations.Add(item);
            }

            root.Set("expectations", expectations);
            return root;
        }
    }
}
