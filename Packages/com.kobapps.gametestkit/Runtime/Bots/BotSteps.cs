using System;
using System.Collections;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>The <c>runBot</c> verb — plug a persona into a test.</summary>
    internal static class BotSteps
    {
        internal static void RegisterAll()
        {
            StepRegistry.Register(new StepDefinition
            {
                Key = "runBot",
                Category = "Bots",
                Summary = "Let a bot play the game with real input, and report what it found.",
                Parameters = new[]
                {
                    new StepParameter("runBot", "string", "Bot name — a GameBot asset in Resources/GameBots.", true),
                    new StepParameter("until", "expression", "Stop once this is true. The bot's goal.", false),
                    new StepParameter("failIf", "expression", "Stop and report a finding when this becomes true.", false),
                    new StepParameter("seconds", "number", "Wall-clock budget.", false, "120"),
                    new StepParameter("actions", "number", "Action budget. 0 uses the bot's own limit.", false, "0"),
                    new StepParameter("expect", "string",
                        "goal (default) — fail unless the goal is met; " +
                        "clean — fail only on findings; " +
                        "explore — never fail, just report.", false, "goal"),
                    new StepParameter("stuckAfter", "number", "Barren actions before calling it a dead end.", false, "12"),
                    new StepParameter("loopAfter", "number",
                        "Times the same screen may come back within the recent window before the run is " +
                        "called a circle — open the store, close the store, repeat. 0 turns it off.",
                        false, "3"),
                    new StepParameter("speed", "number",
                        "Game clock multiplier while the bot plays. 4 makes a soak four times shorter; " +
                        "a failure that only reproduces above 1 is suspect until it reproduces at 1.",
                        false, "1"),
                    new StepParameter("inputSpeed", "number",
                        "Gesture duration multiplier. Below 1 the bot drags and taps faster.", false, "1"),
                },
                Example = "{ \"runBot\": \"Casual\", \"until\": \"ms.wins >= 1\", \"seconds\": 120 }",
                Factory = json =>
                {
                    var seconds = StepJson.Float(json, "seconds", 120f);
                    return new RunBotStep
                    {
                        BotName = StepJson.RequiredString(json, "runBot", "runBot"),
                        Options = new BotRunOptions
                        {
                            Goal = StepJson.OptionalString(json, "until", ""),
                            FailIf = StepJson.OptionalString(json, "failIf", ""),
                            MaxSeconds = seconds,
                            MaxActions = StepJson.Int(json, "actions", 0),
                            StuckAfterBarrenActions = Math.Max(2, StepJson.Int(json, "stuckAfter", 12)),
                            LoopVisitsBeforeFinding = StepJson.Int(json, "loopAfter", 3),
                            TimeScale = StepJson.Float(json, "speed", 0f),
                            InputSpeedScale = StepJson.Float(json, "inputSpeed", 1f),
                        },
                        Expect = StepJson.OptionalString(json, "expect", "goal"),
                        // The runner's 15s default would cut the bot off long before its own budget.
                        TimeoutSeconds = seconds + 30f,
                    };
                },
            });
        }

        /// <summary>
        /// Runs a bot and decides whether what it found should fail the test.
        /// </summary>
        /// <remarks>
        /// Three expectations, because bots get used for two different jobs. <c>goal</c> is an end-to-end
        /// assertion — a persona must be able to complete a flow, and not completing it is the bug.
        /// <c>explore</c> is a soak: run it, write down everything odd, never fail, because the value is
        /// the report and a red build for "the bot wandered off" trains people to ignore it. <c>clean</c>
        /// sits between: go where you like, but no errors and no dead ends.
        /// </remarks>
        private sealed class RunBotStep : TestStep
        {
            public string BotName;
            public BotRunOptions Options;
            public string Expect;

            public override string Describe() => $"run the {BotName} bot";

            public override IEnumerator Execute(TestContext ctx)
            {
                var bot = BotRegistry.Require(BotName);

                BotRunReport report = null;
                var driver = new BotDriver(ctx, Options);
                yield return driver.Run(bot, r => report = r);

                if (report == null) throw new TestFailureException($"The {BotName} bot produced no report.");

                var path = WriteReport(ctx, report);
                if (path != null) ctx.CurrentStep?.Artifacts.Add(path);

                ctx.Log(report.ToText());

                switch ((Expect ?? "goal").Trim().ToLowerInvariant())
                {
                    case "explore":
                        yield break;

                    case "clean":
                        if (report.HasFindings) throw Failure(report);
                        yield break;

                    default:
                        if (report.Outcome != BotOutcome.GoalReached && report.Outcome != BotOutcome.Done)
                            throw Failure(report);
                        if (report.HasFindings) throw Failure(report);
                        yield break;
                }
            }

            private static TestFailureException Failure(BotRunReport report)
            {
                var detail = report.Findings.Count > 0
                    ? "\n  " + string.Join("\n  ", report.Findings)
                    : "";
                return new TestFailureException(
                    $"{report.BotName}: {report.Outcome} after {report.ActionsTaken} actions — " +
                    $"{report.Summary}{detail}");
            }

            /// <summary>Files the full action trail beside the run's screenshots.</summary>
            private static string WriteReport(TestContext ctx, BotRunReport report)
            {
                try
                {
                    if (ctx.Artifacts == null) return null;
                    var folder = ctx.Artifacts.RunFolder;
                    if (string.IsNullOrEmpty(folder)) return null;

                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder,
                        $"bot-{ArtifactStore.Sanitize(report.BotName)}.json");
                    File.WriteAllText(path, report.ToJson().ToJson());
                    return path;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GameTestKit] Could not write the bot report: {e.Message}");
                    return null;
                }
            }
        }
    }
}
