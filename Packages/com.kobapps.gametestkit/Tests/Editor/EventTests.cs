using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using NUnit.Framework;

namespace Kobapps.GameTestKit.Tests
{
    /// <summary>The property matcher grammar — the part a case file is actually written in.</summary>
    public class EventMatcherTests
    {
        private static Dictionary<string, object> Props(params object[] pairs)
        {
            var map = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) map[(string)pairs[i]] = pairs[i + 1];
            return map;
        }

        private static bool Matches(string matcherJson, params object[] pairs) =>
            EventPropertyMatcher.Match("k", JsonValue.Parse(matcherJson), Props(pairs)).Passed;

        [Test]
        public void LiteralsCompareByValue()
        {
            Assert.That(Matches("\"Win\"", "k", "Win"), Is.True);
            Assert.That(Matches("\"Win\"", "k", "Lose"), Is.False);
            Assert.That(Matches("1", "k", 1), Is.True);
            Assert.That(Matches("1", "k", 2), Is.False);
            Assert.That(Matches("true", "k", true), Is.True);
            Assert.That(Matches("true", "k", false), Is.False);
        }

        [Test]
        public void StringComparisonIsCaseInsensitiveLikeTheExpressionEngine()
        {
            Assert.That(Matches("\"win\"", "k", "Win"), Is.True);
        }

        [Test]
        public void NumbersCompareAcrossTypes()
        {
            Assert.That(Matches("40", "k", 40L), Is.True);
            Assert.That(Matches("40", "k", 40.0), Is.True);
            Assert.That(Matches("40", "k", 40f), Is.True);
            Assert.That(Matches("40", "k", "40"), Is.True, "a number that arrived as a string is still that number");
        }

        [Test]
        public void StarMeansPresentAndNonNull()
        {
            Assert.That(Matches("\"*\"", "k", "anything"), Is.True);
            Assert.That(Matches("\"*\"", "k", 0), Is.True, "zero is a value");
            Assert.That(Matches("\"*\"", "other", 1), Is.False);
            Assert.That(Matches("\"*\"", "k", null), Is.False);
        }

        [Test]
        public void BangMeansTheseMustNotBeStamped()
        {
            Assert.That(Matches("\"!\"", "other", 1), Is.True);
            Assert.That(Matches("\"!\"", "k", null), Is.True);
            Assert.That(Matches("\"!\"", "k", "here"), Is.False);
        }

        [Test]
        public void NumericBoundsCatchTheZeroThatIsStillANumber()
        {
            Assert.That(Matches("\">0\"", "k", 0.5), Is.True);
            Assert.That(Matches("\">0\"", "k", 0), Is.False, "a duration of zero is the bug this form exists for");
            Assert.That(Matches("\">=1\"", "k", 1), Is.True);
            Assert.That(Matches("\"<10\"", "k", 9), Is.True);
            Assert.That(Matches("\"<=3\"", "k", 3), Is.True);
            Assert.That(Matches("\"!=0\"", "k", 1), Is.True);
            Assert.That(Matches("\"!=0\"", "k", 0), Is.False);
        }

        [Test]
        public void ABoundAgainstSomethingUnComparableFailsRatherThanThrowing()
        {
            var match = EventPropertyMatcher.Match("k", JsonValue.Parse("\">0\""), Props("k", "Win"));

            Assert.That(match.Passed, Is.False);
            Assert.That(match.Reason, Does.Contain("not a number"));
        }

        [Test]
        public void RegexMatchesOnTheRenderedValue()
        {
            Assert.That(Matches("\"~^Level_\"", "k", "Level_End"), Is.True);
            Assert.That(Matches("\"~^Level_\"", "k", "Session_Start"), Is.False);
        }

        [Test]
        public void AnArrayIsOneOf()
        {
            Assert.That(Matches("[\"Win\",\"Fail\",\"Quit\"]", "k", "Fail"), Is.True);
            Assert.That(Matches("[\"Win\",\"Fail\"]", "k", "Abandoned"), Is.False);
            Assert.That(Matches("[1,2,3]", "k", 2), Is.True);
        }

        [Test]
        public void TypeChecksTheShapeOnly()
        {
            Assert.That(Matches("\"type:number\"", "k", 12), Is.True);
            Assert.That(Matches("\"type:number\"", "k", "twelve"), Is.False);
            Assert.That(Matches("\"type:string\"", "k", "twelve"), Is.True);
            Assert.That(Matches("\"type:bool\"", "k", true), Is.True);
            Assert.That(Matches("\"type:list\"", "k", new[] { 1, 2 }), Is.True);
            Assert.That(Matches("\"type:list\"", "k", "not a list"), Is.False);
        }

        [Test]
        public void AnUnknownTypeNameSaysWhatIsAllowed()
        {
            var match = EventPropertyMatcher.Match("k", JsonValue.Parse("\"type:widget\""), Props("k", 1));

            Assert.That(match.Passed, Is.False);
            Assert.That(match.Reason, Does.Contain("number"), "the message has to name the valid options");
        }

        /// <summary>The controlled vocabulary a game declares, used by the <c>@const:</c> form below.</summary>
        public static class Verdicts
        {
            public const string RESULT_WIN = "Win";
            public const string RESULT_FAIL = "Fail";
            public const string OTHER = "Ignored";
        }

        [Test]
        public void ConstMatchesTheValuesAClassDeclares()
        {
            const string reference = "\"@const:Kobapps.GameTestKit.Tests.EventMatcherTests+Verdicts.RESULT_*\"";

            Assert.That(Matches(reference, "k", "Win"), Is.True);
            Assert.That(Matches(reference, "k", "Fail"), Is.True);
            Assert.That(Matches(reference, "k", "Ignored"), Is.False, "OTHER is not one of the RESULT_ constants");
        }

        [Test]
        public void ConstNamesTheAllowedValuesWhenItFails()
        {
            var match = EventPropertyMatcher.Match("k",
                JsonValue.Parse("\"@const:Kobapps.GameTestKit.Tests.EventMatcherTests+Verdicts.RESULT_*\""),
                Props("k", "win-ish"));

            Assert.That(match.Passed, Is.False);
            Assert.That(match.Reason, Does.Contain("Win").And.Contain("Fail"));
        }

        [Test]
        public void AnUnknownTypeInAConstReferenceFailsWithAReason()
        {
            var match = EventPropertyMatcher.Match("k",
                JsonValue.Parse("\"@const:Nope.NotAType.THING_*\""), Props("k", "x"));

            Assert.That(match.Passed, Is.False);
            Assert.That(match.Reason, Does.Contain("no type named"));
        }

        [Test]
        public void MatchAllReportsEveryKeyInTheMatcher()
        {
            var results = EventPropertyMatcher.MatchAll(
                JsonValue.Parse("{ \"a\": 1, \"b\": \"*\", \"c\": \"!\" }"),
                Props("a", 1, "b", "there"));

            Assert.That(results.Count, Is.EqualTo(3));
            Assert.That(results.TrueForAll(r => r.Passed), Is.True);
        }
    }

    /// <summary>The event window: ordering, marks and per-name lookup.</summary>
    public class TestEventLogTests
    {
        [SetUp]
        public void Setup()
        {
            TestEventLog.Enabled = true;
            TestEventLog.Clear();
        }

        private static Dictionary<string, object> Props(string key, object value) =>
            new Dictionary<string, object> { { key, value } };

        [Test]
        public void EventsAreNumberedInTheOrderTheyArrive()
        {
            var first = TestEventLog.Record("A");
            var second = TestEventLog.Record("B");

            Assert.That(second.Sequence, Is.GreaterThan(first.Sequence));
            Assert.That(TestEventLog.Count, Is.EqualTo(2));
        }

        [Test]
        public void APropertyMapIsCopiedSoALaterMutationCannotRewriteHistory()
        {
            var live = new Dictionary<string, object> { { "level", 1 } };
            var captured = TestEventLog.Record("Level_Start", live);

            live["level"] = 99;

            captured.TryGet("level", out var recorded);
            Assert.That(recorded, Is.EqualTo(1), "an event bus that reuses one dictionary must not rewrite the log");
        }

        [Test]
        public void AMarkSeparatesWhatCameBeforeFromWhatCameAfter()
        {
            TestEventLog.Record("Before");
            var mark = TestEventLog.Mark();
            TestEventLog.Record("After");

            Assert.That(TestEventLog.CountOf("Before", mark), Is.EqualTo(0));
            Assert.That(TestEventLog.CountOf("After", mark), Is.EqualTo(1));
            Assert.That(TestEventLog.Since(mark).Count, Is.EqualTo(1));
        }

        [Test]
        public void ANamedMarkCanBeReadBackAndMoved()
        {
            TestEventLog.Record("One");
            TestEventLog.Mark("case");
            TestEventLog.Record("Two");

            Assert.That(TestEventLog.CountOf("Two", TestEventLog.MarkOf("case")), Is.EqualTo(1));

            TestEventLog.Mark("case");
            Assert.That(TestEventLog.CountOf("Two", TestEventLog.MarkOf("case")), Is.EqualTo(0),
                "re-marking moves it to now");

            Assert.That(TestEventLog.MarkOf("never set"), Is.EqualTo(-1));
        }

        [Test]
        public void LookupByNameIsCaseInsensitive()
        {
            TestEventLog.Record("Level_End");
            Assert.That(TestEventLog.CountOf("level_end"), Is.EqualTo(1));
        }

        [Test]
        public void DescribeListsWhatActuallyFired()
        {
            TestEventLog.Record("A");
            TestEventLog.Record("B");

            Assert.That(TestEventLog.Describe(), Is.EqualTo("A, B"));
            Assert.That(TestEventLog.Describe(TestEventLog.LastSequence), Is.EqualTo("(no events at all)"));
        }

        [Test]
        public void CapacityDropsTheOldestFirst()
        {
            TestEventLog.Capacity = 2;
            try
            {
                TestEventLog.Record("A");
                TestEventLog.Record("B");
                TestEventLog.Record("C");

                Assert.That(TestEventLog.Count, Is.EqualTo(2));
                Assert.That(TestEventLog.CountOf("A"), Is.EqualTo(0));
                Assert.That(TestEventLog.CountOf("C"), Is.EqualTo(1));
            }
            finally
            {
                TestEventLog.Capacity = 0;
            }
        }

        [Test]
        public void DeliveryRecordsWhichSinkTookIt()
        {
            var captured = TestEventLog.Record("Purchase", Props("sku", "gems_10"), new[]
            {
                new TestEventDelivery("Mixpanel"),
                new TestEventDelivery("Singular", false, "no consent"),
            });

            Assert.That(captured.WasDeliveredTo("mixpanel"), Is.True);
            Assert.That(captured.WasDeliveredTo("Singular"), Is.False);
            Assert.That(captured.DeliveryTo("Singular").Reason, Is.EqualTo("no consent"));
            Assert.That(captured.DeliveryTo("Firebase"), Is.Null, "a sink that never saw it has no record");
        }

        [Test]
        public void RecordingIsANoOpWhileDisabled()
        {
            TestEventLog.Enabled = false;
            try
            {
                Assert.That(TestEventLog.Record("A"), Is.Null);
                Assert.That(TestEventLog.Count, Is.EqualTo(0));
            }
            finally
            {
                TestEventLog.Enabled = true;
            }
        }
    }

    /// <summary>
    /// The verbs as a case file writes them. These lock the JSON shape down: the parameters are flat,
    /// like every other step in the kit, so <c>{"step": "expectEvent"}</c> works too.
    /// </summary>
    public class EventStepParsingTests
    {
        private static GameTest Parse(string steps) =>
            TestScriptParser.ParseTest("{ \"steps\": [ " + steps + " ] }", "events.gametest.json");

        [Test]
        public void TheVerbsAreRegisteredAndParse()
        {
            var test = Parse(@"
                { ""eventCase"": ""Level_End"", ""case"": ""win"", ""companions"": [""Level_Start""] },
                { ""waitForEvent"": ""Level_End"", ""within"": 20 },
                { ""expectEvent"": ""Level_End"", ""count"": 1,
                  ""props"": { ""Result"": ""Win"", ""Duration"": "">0"" },
                  ""delivered"": [""Mixpanel""], ""notDelivered"": [""Singular""] },
                { ""expectNoEvent"": ""legacy_level_end"" },
                { ""expectOrder"": [""Level_Start"", ""Level_End""] },
                { ""expectOnlyExpectedEvents"": true },
                { ""eventInvariants"": true, ""pairs"": [[""Level_Start"", ""Level_End""]] },
                { ""eventProof"": true }");

            Assert.That(test.Steps.Count, Is.EqualTo(8));
        }

        [Test]
        public void TheStepFormWorksToo()
        {
            var test = Parse(@"{ ""step"": ""expectEvent"", ""expectEvent"": ""Level_End"", ""count"": 2 }");

            Assert.That(test.Steps.Count, Is.EqualTo(1));
            Assert.That(test.Steps[0].Describe(), Does.Contain("Level_End"));
        }

        [Test]
        public void AVerbWithoutItsEventIsRejectedWhileParsing()
        {
            Assert.That(() => Parse(@"{ ""expectEvent"": """" }"), Throws.TypeOf<TestFailureException>());
        }

        [Test]
        public void ExpectOrderNeedsTwoNames()
        {
            Assert.That(() => Parse(@"{ ""expectOrder"": [""only-one""] }"),
                Throws.TypeOf<TestFailureException>());
        }

        [Test]
        public void FixturesParseIntoSteps()
        {
            var steps = TestScriptParser.ParseFixture(@"[ { ""wait"": 1 } ]", "beforeEach");
            Assert.That(steps.Count, Is.EqualTo(1));

            Assert.That(TestScriptParser.ParseFixture("", "beforeEach"), Is.Empty);
            Assert.That(TestScriptParser.ParseFixture(null, "beforeEach"), Is.Empty);
        }

        [Test]
        public void ABrokenFixtureIsNamedInTheError()
        {
            var problem = Assert.Throws<TestFailureException>(
                () => TestScriptParser.ParseFixture(@"[ { ""nonsenseVerb"": 1 } ]", "beforeEach"));

            Assert.That(problem.Message, Does.Contain("beforeEach").And.Contain("#1"));
        }

        [Test]
        public void AFixtureThatIsNotAnArrayIsRejected()
        {
            Assert.That(() => TestScriptParser.ParseFixture(@"{ ""wait"": 1 }", "afterEach"),
                Throws.TypeOf<TestFailureException>());
        }

        [Test]
        public void ASuiteCarriesItsFixturesThroughOptions()
        {
            var suite = TestScriptParser.ParseSuite(@"{
                ""name"": ""Analytics"",
                ""beforeEach"": [ { ""call"": ""resetSave"" } ],
                ""afterEach"": [ { ""call"": ""toMenu"" } ]
            }", "a.gamesuite.json");

            Assert.That(TestScriptParser.ParseFixture(suite.Options.BeforeEachJson, "beforeEach").Count,
                Is.EqualTo(1));
            Assert.That(TestScriptParser.ParseFixture(suite.Options.AfterEachJson, "afterEach").Count,
                Is.EqualTo(1));
        }

        [Test]
        public void FixturesSurviveTheRoundTripToPlayMode()
        {
            var options = new RunOptions { BeforeEachJson = @"[{""wait"":1}]" };

            var restored = TestScriptParser.ParseOptions(
                JsonValue.Parse(TestScriptParser.WriteOptions(options).ToJson()), new RunOptions());

            Assert.That(TestScriptParser.ParseFixture(restored.BeforeEachJson, "beforeEach").Count, Is.EqualTo(1));
        }
    }

    /// <summary>The session-wide sweep — the defects no single payload reveals.</summary>
    public class EventInvariantTests
    {
        private static TestEvent At(long sequence, string name, double msFromStart = 0,
            Dictionary<string, object> props = null)
        {
            var captured = new TestEvent(name, props, timestampUtc: System.DateTime.UnixEpoch.AddMilliseconds(msFromStart));
            captured.Sequence = sequence;
            return captured;
        }

        [Test]
        public void AGapInTheSequenceIsReported()
        {
            var violations = EventInvariants.Run(new[] { At(1, "A"), At(4, "B") });

            Assert.That(violations.Count, Is.EqualTo(1));
            Assert.That(violations[0], Does.Contain("2 event(s) are missing"));
        }

        [Test]
        public void AContiguousSequenceIsClean()
        {
            Assert.That(EventInvariants.Run(new[] { At(1, "A"), At(2, "B"), At(3, "C") }), Is.Empty);
        }

        [Test]
        public void TwoIdenticalPayloadsCloseTogetherReadAsADoubleSend()
        {
            var props = new Dictionary<string, object> { { "id", 7 } };

            var violations = EventInvariants.Run(new[]
            {
                At(1, "Level_Start", 0, props),
                At(2, "Level_Start", 40, props),
            });

            Assert.That(violations.Count, Is.EqualTo(1));
            Assert.That(violations[0], Does.Contain("twice with an identical payload"));
        }

        [Test]
        public void TheSameEventLaterIsAPlayerDoingSomethingTwice()
        {
            var props = new Dictionary<string, object> { { "id", 7 } };

            var violations = EventInvariants.Run(new[]
            {
                At(1, "Level_Start", 0, props),
                At(2, "Level_Start", 5000, props),
            });

            Assert.That(violations, Is.Empty);
        }

        [Test]
        public void DifferentPayloadsAreNotDuplicates()
        {
            var violations = EventInvariants.Run(new[]
            {
                At(1, "Level_Start", 0, new Dictionary<string, object> { { "id", 1 } }),
                At(2, "Level_Start", 10, new Dictionary<string, object> { { "id", 2 } }),
            });

            Assert.That(violations, Is.Empty);
        }

        [Test]
        public void APhantomOpenIsReported()
        {
            var options = new EventInvariants.Options { CheckDuplicates = false };
            options.Pairs.Add(new KeyValuePair<string, string>("Level_Start", "Level_End"));

            var violations = EventInvariants.Run(new[]
            {
                At(1, "Level_Start"), At(2, "Level_Start"), At(3, "Level_End"),
            }, options);

            Assert.That(violations.Count, Is.EqualTo(1));
            Assert.That(violations[0], Does.Contain("still open"));
        }

        [Test]
        public void ACloseWithoutAnOpenIsReported()
        {
            var options = new EventInvariants.Options();
            options.Pairs.Add(new KeyValuePair<string, string>("Level_Start", "Level_End"));

            var violations = EventInvariants.Run(new[] { At(1, "Level_End") }, options);

            Assert.That(violations.Count, Is.EqualTo(1));
            Assert.That(violations[0], Does.Contain("never opened"));
        }

        [Test]
        public void BalancedPairsAreClean()
        {
            var options = new EventInvariants.Options();
            options.Pairs.Add(new KeyValuePair<string, string>("Level_Start", "Level_End"));

            // Spaced past the duplicate window on purpose: two payload-identical Level_Starts a few
            // milliseconds apart really would be a double-send, and the sweep is right to say so.
            Assert.That(EventInvariants.Run(new[]
            {
                At(1, "Level_Start", 0), At(2, "Level_End", 1000),
                At(3, "Level_Start", 2000), At(4, "Level_End", 3000),
            }, options), Is.Empty);
        }

        [Test]
        public void PayloadLessEventsRepeatedInstantlyStillReadAsADoubleSend()
        {
            var violations = EventInvariants.Run(new[] { At(1, "Session_Start", 0), At(2, "Session_Start", 10) });

            Assert.That(violations.Count, Is.EqualTo(1),
                "an event with no properties at all is still the same event twice");
        }

        [Test]
        public void ARequiredPropertyMissingAnywhereIsReported()
        {
            var options = new EventInvariants.Options();
            options.RequiredProperties.Add("Session_Id");
            options.RequiredPropertyExemptions.Add("App_Install");

            var violations = EventInvariants.Run(new[]
            {
                At(1, "App_Install"),
                At(2, "Level_Start", 0, new Dictionary<string, object> { { "Session_Id", "abc" } }),
                At(3, "Level_End"),
            }, options);

            Assert.That(violations.Count, Is.EqualTo(1), "the exempt event does not count");
            Assert.That(violations[0], Does.Contain("Level_End").And.Contain("Session_Id"));
        }

        [Test]
        public void GapsAreLookedForInTheGamesOwnCounterWhenThereIsOne()
        {
            var options = new EventInvariants.Options { SequenceProperty = "Event_Number" };

            // Contiguous in the log — it numbers what it receives — but the game's own counter skipped
            // 2, which is the only trace of an event that was emitted and never recorded.
            var violations = EventInvariants.Run(new[]
            {
                At(1, "A", 0, new Dictionary<string, object> { { "Event_Number", 1 } }),
                At(2, "B", 1000, new Dictionary<string, object> { { "Event_Number", 3 } }),
            }, options);

            Assert.That(violations.Count, Is.EqualTo(1));
            Assert.That(violations[0], Does.Contain("1 event(s) are missing"));
        }

        [Test]
        public void AContiguousGameCounterIsClean()
        {
            var options = new EventInvariants.Options { SequenceProperty = "Event_Number" };

            Assert.That(EventInvariants.Run(new[]
            {
                At(1, "A", 0, new Dictionary<string, object> { { "Event_Number", 1 } }),
                At(2, "B", 1000, new Dictionary<string, object> { { "Event_Number", 2 } }),
            }, options), Is.Empty);
        }

        [Test]
        public void EventsWithoutTheCounterAreSkippedRatherThanReadAsAHole()
        {
            var options = new EventInvariants.Options { SequenceProperty = "Event_Number" };

            Assert.That(EventInvariants.Run(new[]
            {
                At(1, "A", 0, new Dictionary<string, object> { { "Event_Number", 1 } }),
                At(2, "NoCounter", 1000),
                At(3, "B", 2000, new Dictionary<string, object> { { "Event_Number", 2 } }),
            }, options), Is.Empty);
        }

        [Test]
        public void TheCounterDoesNotDefeatDuplicateDetection()
        {
            // Every real event carries a counter that differs by construction. Comparing it as part
            // of the payload would make every event unique and silently switch this check off.
            var options = new EventInvariants.Options { SequenceProperty = "Event_Number" };

            var violations = EventInvariants.Run(new[]
            {
                At(1, "Purchase", 0, new Dictionary<string, object> { { "Item", "Sword" }, { "Event_Number", 1 } }),
                At(2, "Purchase", 30, new Dictionary<string, object> { { "Item", "Sword" }, { "Event_Number", 2 } }),
            }, options);

            Assert.That(violations.Count, Is.EqualTo(1));
            Assert.That(violations[0], Does.Contain("identical payload"));
        }

        [Test]
        public void ExtraVolatilePropertiesCanBeIgnoredToo()
        {
            var options = new EventInvariants.Options();
            options.IgnoreInDuplicates.Add("Sent_At");

            var violations = EventInvariants.Run(new[]
            {
                At(1, "Ping", 0, new Dictionary<string, object> { { "Sent_At", 1000 } }),
                At(2, "Ping", 30, new Dictionary<string, object> { { "Sent_At", 1030 } }),
            }, options);

            Assert.That(violations.Count, Is.EqualTo(1));
        }

        [Test]
        public void SummarizeReadsAsOneMessage()
        {
            var text = EventInvariants.Summarize(new[] { "one thing", "another" });

            Assert.That(text, Does.StartWith("2 invariants broken:"));
            Assert.That(text, Does.Contain("• one thing"));
        }
    }
}
