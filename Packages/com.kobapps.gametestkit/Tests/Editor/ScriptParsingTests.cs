using Kobapps.GameTestKit.Scripting;
using NUnit.Framework;

namespace Kobapps.GameTestKit.Tests
{
    public class ScriptParsingTests
    {
        [Test]
        public void ParsesAFullTest()
        {
            var test = TestScriptParser.ParseTest(@"{
                ""name"": ""Buy a sword"",
                ""description"": ""Spends gold"",
                ""tags"": [""smoke"", ""shop""],
                ""scene"": ""Shop"",
                ""timeout"": 45,
                ""retries"": 2,
                ""setup"": [ { ""call"": ""grantGold"", ""args"": [100] } ],
                ""steps"": [
                    { ""click"": ""id:shop_button"" },
                    { ""waitForVisible"": ""id:shop_panel"", ""timeout"": 8 },
                    { ""assert"": ""gold == 40"" }
                ],
                ""teardown"": [ { ""call"": ""reset"" } ]
            }", "buy.gametest.json");

            Assert.That(test.Name, Is.EqualTo("Buy a sword"));
            Assert.That(test.Scene, Is.EqualTo("Shop"));
            Assert.That(test.TimeoutSeconds, Is.EqualTo(45f));
            Assert.That(test.Retries, Is.EqualTo(2));
            Assert.That(test.Tags, Is.EquivalentTo(new[] { "smoke", "shop" }));
            Assert.That(test.HasTag("SMOKE"), Is.True, "tag matching is case-insensitive");
            Assert.That(test.Setup.Count, Is.EqualTo(1));
            Assert.That(test.Steps.Count, Is.EqualTo(3));
            Assert.That(test.Teardown.Count, Is.EqualTo(1));

            Assert.That(test.Steps[0], Is.TypeOf<ClickStep>());
            Assert.That(((ClickStep)test.Steps[0]).Selector, Is.EqualTo("id:shop_button"));
            Assert.That(((WaitForElementStep)test.Steps[1]).Timeout, Is.EqualTo(8f));
        }

        [Test]
        public void ABareArrayOfStepsIsAValidTest()
        {
            var test = TestScriptParser.ParseTest(@"[ { ""click"": ""#Play"" }, { ""wait"": 1 } ]",
                "Assets/GameTests/quick-check.gametest.json");

            Assert.That(test.Steps.Count, Is.EqualTo(2));
            Assert.That(test.Name, Is.EqualTo("quick check"), "the file name becomes the test name");
        }

        [Test]
        public void StepAliasesResolveToTheSameStep()
        {
            var test = TestScriptParser.ParseTest(@"[ { ""tap"": ""#Play"" }, { ""hover"": ""#Play"" } ]");

            Assert.That(test.Steps[0], Is.TypeOf<ClickStep>());
            Assert.That(((ClickStep)test.Steps[0]).Selector, Is.EqualTo("#Play"));
            Assert.That(test.Steps[1], Is.TypeOf<MoveStep>());
        }

        [Test]
        public void ExplicitStepFieldWorksToo()
        {
            var test = TestScriptParser.ParseTest(@"[ { ""step"": ""click"", ""click"": ""#Play"", ""clicks"": 2 } ]");

            Assert.That(((ClickStep)test.Steps[0]).Clicks, Is.EqualTo(2));
        }

        [Test]
        public void NestedStepsAreParsed()
        {
            var test = TestScriptParser.ParseTest(@"[
                { ""repeat"": 3, ""steps"": [ { ""click"": ""#Buy"" }, { ""wait"": 0.2 } ] }
            ]");

            var repeat = (RepeatStep)test.Steps[0];
            Assert.That(repeat.Times, Is.EqualTo(3));
            Assert.That(repeat.Steps.Count, Is.EqualTo(2));
        }

        [Test]
        public void UnknownStepNamesTheOffendingLineAndListsWhatExists()
        {
            var exception = Assert.Throws<TestFailureException>(() =>
                TestScriptParser.ParseTest(@"[ { ""click"": ""#A"" }, { ""frobnicate"": ""#B"" } ]", "x.gametest.json"));

            Assert.That(exception.Message, Does.Contain("step #2"));
            Assert.That(exception.Message, Does.Contain("click"), "the error should list known steps");
        }

        [Test]
        public void MissingRequiredParameterIsReported()
        {
            var exception = Assert.Throws<TestFailureException>(() =>
                TestScriptParser.ParseTest(@"[ { ""drag"": ""#Card"" } ]"));

            Assert.That(exception.Message, Does.Contain("\"to\""));
        }

        [Test]
        public void ValidateReportsProblemsWithoutThrowing()
        {
            Assert.That(TestScriptParser.Validate(@"[ { ""click"": ""#A"" } ]"), Is.Empty);

            var problems = TestScriptParser.Validate(@"{ ""name"": ""x"" }");
            Assert.That(problems, Is.Not.Empty);
            Assert.That(problems[0], Does.Contain("steps"));
        }

        [Test]
        public void BadJsonPointsAtTheLine()
        {
            var problems = TestScriptParser.Validate("{\n  \"steps\": [ { \"click\" \"#A\" } ]\n}", "broken.gametest.json");

            Assert.That(problems, Is.Not.Empty);
            Assert.That(problems[0], Does.Contain("line 2"));
        }

        [Test]
        public void OptionsRoundTrip()
        {
            var original = new RunOptions
            {
                Retries = 3,
                Shuffle = true,
                Seed = 12345,
                Pointer = PointerMode.Touch,
                Backend = InputBackendKind.EventSystem,
                InputSpeedScale = 2.5f,
                StopOnFirstFailure = true,
                ScreenshotEveryStep = true,
                ShowInputOverlay = false,
            };

            var restored = TestScriptParser.ParseOptions(TestScriptParser.WriteOptions(original), new RunOptions());

            Assert.That(restored.Retries, Is.EqualTo(3));
            Assert.That(restored.Shuffle, Is.True);
            Assert.That(restored.Seed, Is.EqualTo(12345));
            Assert.That(restored.Pointer, Is.EqualTo(PointerMode.Touch));
            Assert.That(restored.Backend, Is.EqualTo(InputBackendKind.EventSystem));
            Assert.That(restored.InputSpeedScale, Is.EqualTo(2.5f));
            Assert.That(restored.StopOnFirstFailure, Is.True);
            Assert.That(restored.ScreenshotEveryStep, Is.True);
            Assert.That(restored.ShowInputOverlay, Is.False);
        }

        [Test]
        public void FiltersSelectByNameAndTag()
        {
            var smoke = new GameTest("Buy a sword");
            smoke.Tags.Add("smoke");

            var nightly = new GameTest("Grind for an hour");
            nightly.Tags.Add("nightly");

            var byTag = new RunOptions();
            byTag.Tags.Add("smoke");
            Assert.That(byTag.Matches(smoke), Is.True);
            Assert.That(byTag.Matches(nightly), Is.False);

            var byName = new RunOptions { NameFilter = "sword" };
            Assert.That(byName.Matches(smoke), Is.True);
            Assert.That(byName.Matches(nightly), Is.False);

            var excluded = new RunOptions();
            excluded.ExcludeTags.Add("nightly");
            Assert.That(excluded.Matches(smoke), Is.True);
            Assert.That(excluded.Matches(nightly), Is.False);
        }

        [Test]
        public void EveryRegisteredStepDocumentsItself()
        {
            Assert.That(StepRegistry.All.Count, Is.GreaterThan(20));

            foreach (var definition in StepRegistry.All)
            {
                Assert.That(definition.Summary, Is.Not.Null.And.Not.Empty,
                    $"step '{definition.Key}' has no summary — it would appear blank in the AI catalogue");
                Assert.That(definition.Factory, Is.Not.Null, $"step '{definition.Key}' has no factory");
                Assert.That(definition.Example, Is.Not.Null.And.Not.Empty,
                    $"step '{definition.Key}' has no example");
            }
        }

        [Test]
        public void CustomStepsCanBeRegisteredAndUsedFromJson()
        {
            StepRegistry.Register(new StepDefinition
            {
                Key = "customTestVerb",
                Summary = "A verb registered by a game.",
                Example = "{ \"customTestVerb\": \"hello\" }",
                Factory = json => new LogStep { Message = json["customTestVerb"].AsString() },
            });

            var test = TestScriptParser.ParseTest(@"[ { ""customTestVerb"": ""hello"" } ]");

            Assert.That(test.Steps[0], Is.TypeOf<LogStep>());
            Assert.That(((LogStep)test.Steps[0]).Message, Is.EqualTo("hello"));
        }
    }
}
