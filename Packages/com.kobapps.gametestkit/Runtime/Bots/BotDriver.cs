using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>How long a bot may play, and what counts as finished.</summary>
    public sealed class BotRunOptions
    {
        /// <summary>Stop once this expression is true. Empty means "play until something else stops it".</summary>
        public string Goal = "";

        /// <summary>Stop when this becomes true, and report it as a finding.</summary>
        public string FailIf = "";

        /// <summary>Wall-clock budget. 0 = no limit.</summary>
        public float MaxSeconds = 120f;

        /// <summary>Action budget. 0 = the bot's own <see cref="GameBot.MaxActions"/>, then no limit.</summary>
        public int MaxActions;

        /// <summary>Consecutive actions that change nothing before the run is called stuck.</summary>
        public int StuckAfterBarrenActions = 12;

        /// <summary>Beats of history examined for a circle.</summary>
        public int LoopWindow = 10;

        /// <summary>A window holding at most this many distinct screens is a circle, not exploration.</summary>
        public int LoopStates = 3;

        /// <summary>
        /// How often the current screen must come back inside the window before it is a finding.
        /// 0 or 1 turns loop detection off.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="StuckAfterBarrenActions"/> because a circle is not barren: every beat
        /// of "open the store, close the store" changes the screen, so the dead-end detector counts it as
        /// progress and the run burns its whole budget before reporting anything.
        /// </remarks>
        public int LoopVisitsBeforeFinding = 3;

        /// <summary>
        /// How long to wait for a bot's chosen target before giving up on that action.
        /// </summary>
        /// <remarks>
        /// Much shorter than a scripted step's, because the two are asking different questions. A test says
        /// "this button must appear" and should wait. A bot picked this target from a snapshot a moment
        /// ago and will pick again next beat — waiting five seconds for a tile that has already been
        /// merged away just burns the run's budget.
        /// </remarks>
        public float TargetTimeoutSeconds = 1.5f;

        /// <summary>
        /// Consecutive unreachable targets before it counts as a finding.
        /// </summary>
        /// <remarks>
        /// One miss is churn — the board moved under the pointer. A run of them is the real bug: something
        /// is covering the UI, or every element the bot can see is dead.
        /// </remarks>
        public int UnreachableStreakBeforeFinding = 4;

        /// <summary>End the run when the game logs an error.</summary>
        public bool StopOnLoggedError = true;

        /// <summary>Capture a screenshot each time a finding is raised.</summary>
        public bool ScreenshotOnFinding = true;

        /// <summary>
        /// Time.timeScale for the bot's run. 1 = real time; 0 leaves it alone.
        /// </summary>
        /// <remarks>
        /// A soak that has to play twenty levels at human speed is a soak nobody runs. Raising the game
        /// clock speeds up everything the game does — animations, cannon fire, regeneration — while the
        /// bot's own gestures stay real device input, so what is being exercised does not change, only how
        /// long it takes. Push it too far and physics and frame-rate-sensitive code start behaving
        /// differently, which is a real risk: treat a failure that only reproduces at high speed as
        /// suspect until it reproduces at 1.
        /// </remarks>
        public float TimeScale;

        /// <summary>Multiplies gesture durations. Below 1 the bot's drags and taps are quicker.</summary>
        public float InputSpeedScale = 1f;
    }

    /// <summary>
    /// Runs a bot: sense, decide, perform as real input, look for trouble, repeat.
    /// </summary>
    /// <remarks>
    /// The driver is the only thing that touches the game, and it only ever does so through
    /// <see cref="VirtualUser"/> and the locator — so a bot cannot accidentally acquire powers a player
    /// does not have. It also owns the judgement a bot should not make: a bot reporting
    /// <see cref="BotActionKind.Stuck"/> is not automatically a defect, because the game may legitimately
    /// be mid-animation. The driver decides by watching whether the world is still changing on its own,
    /// which a single bot decision cannot see.
    /// </remarks>
    public sealed class BotDriver
    {
        private readonly TestContext _ctx;
        private readonly BotRunOptions _options;

        public BotDriver(TestContext ctx, BotRunOptions options = null)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _options = options ?? new BotRunOptions();
        }

        public IEnumerator Run(GameBot bot, Action<BotRunReport> onFinished)
        {
            if (bot == null) throw new TestFailureException("No bot to run.");

            int seed = _ctx.Random.Next();
            var report = new BotRunReport
            {
                BotName = bot.BotName,
                Persona = bot.Persona,
                Seed = seed,
            };

            var context = new BotContext(new System.Random(seed)) { StartedAt = Time.realtimeSinceStartup };
            context.Journal = message => _ctx.Log($"[{bot.BotName}] {message}");

            var tracked = new List<string>(bot.TrackedValues());
            int actionBudget = _options.MaxActions > 0 ? _options.MaxActions : bot.MaxActions;
            float deadline = _options.MaxSeconds > 0
                ? Time.realtimeSinceStartup + _options.MaxSeconds
                : float.MaxValue;

            float originalTimeScale = Time.timeScale;
            float originalInputSpeed = _ctx.Options.InputSpeedScale;
            if (_options.TimeScale > 0f) Time.timeScale = _options.TimeScale;
            if (_options.InputSpeedScale > 0f) _ctx.Options.InputSpeedScale = _options.InputSpeedScale;

            bot.OnRunStarted(context);
            _ctx.Log($"[{bot.BotName}] started — {(string.IsNullOrEmpty(bot.Persona) ? "no persona" : bot.Persona)}");

            string previousFingerprint = Fingerprint(tracked);
            int unreachableStreak = 0;
            var loops = new LoopWatch(_options.LoopWindow, _options.LoopStates,
                _options.LoopVisitsBeforeFinding);

            while (true)
            {
                if (!string.IsNullOrEmpty(_options.Goal) && context.Check(_options.Goal))
                {
                    Finish(report, BotOutcome.GoalReached, $"Goal '{_options.Goal}' met.");
                    break;
                }

                if (!string.IsNullOrEmpty(_options.FailIf) && context.Check(_options.FailIf))
                {
                    Raise(report, context, $"The fail condition '{_options.FailIf}' became true.");
                    yield return Screenshot(report, "fail-condition");
                    Finish(report, BotOutcome.Faulted, $"Fail condition '{_options.FailIf}' met.");
                    break;
                }

                var loggedError = _ctx.Logs?.FirstError;
                if (loggedError != null)
                {
                    Raise(report, context, $"The game logged an error: {loggedError.Message}");
                    context.Errors.Add(loggedError.Message);
                    _ctx.Logs.ClearPendingError();
                    yield return Screenshot(report, "logged-error");

                    if (_options.StopOnLoggedError)
                    {
                        Finish(report, BotOutcome.Faulted, "The game logged an error while the bot played.");
                        break;
                    }
                }

                // Checked here rather than after each action so both branches below feed one detector:
                // a circle can be made of taps, of stuck beats, or of a mix of the two.
                if (loops.IsGoingInCircles(out int screens, out int visits))
                {
                    Raise(report, context,
                        $"Going in circles: the last {_options.LoopWindow} beats only ever reached " +
                        $"{screens} screens, and the bot has come back to this one {visits} times. " +
                        DescribeCycle(report, _options.LoopWindow));
                    yield return Screenshot(report, "loop");
                    Finish(report, BotOutcome.Stuck,
                        $"The bot went round in circles between {screens} screens.");
                    break;
                }

                if (Time.realtimeSinceStartup > deadline)
                {
                    Finish(report, BotOutcome.BudgetSpent,
                        $"Time budget of {_options.MaxSeconds:0.#}s spent.");
                    break;
                }

                if (actionBudget > 0 && context.ActionCount >= actionBudget)
                {
                    Finish(report, BotOutcome.BudgetSpent, $"Action budget of {actionBudget} spent.");
                    break;
                }

                BotAction action;
                try
                {
                    action = bot.Decide(context) ?? BotAction.Wait();
                }
                catch (Exception e)
                {
                    Raise(report, context, $"The bot threw while deciding: {e.Message}");
                    Finish(report, BotOutcome.Faulted, "The bot threw while deciding.");
                    break;
                }

                if (action.Kind == BotActionKind.Done)
                {
                    Finish(report, BotOutcome.Done, action.Reason ?? "The bot reported it was finished.");
                    break;
                }

                var step = new BotStep
                {
                    Index = context.ActionCount,
                    AtSeconds = context.Elapsed,
                    Action = action.Describe(),
                    Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                };
                foreach (var path in tracked) step.Values[path] = Describe(GameTestBindings.TryGetValue(path, out var v) ? v : null);

                if (action.Kind == BotActionKind.Stuck)
                {
                    // Not a verdict on its own — the game may simply be mid-animation. Let it run, and only
                    // call it stuck when the world stops moving on its own too.
                    yield return Settle(0.75f);
                    var settled = Fingerprint(tracked);
                    bool worldMoved = settled != previousFingerprint;
                    previousFingerprint = settled;
                    loops.Record(settled);

                    step.ChangedSomething = worldMoved;
                    step.Problem = worldMoved ? null : action.Reason ?? "the bot saw nothing to do";
                    report.Steps.Add(step);
                    context.ActionCount++;

                    if (worldMoved) { context.BarrenActions = 0; continue; }

                    context.BarrenActions++;
                    if (context.BarrenActions >= _options.StuckAfterBarrenActions)
                    {
                        Raise(report, context,
                            $"Dead end: the bot found nothing to do for {context.BarrenActions} beats and " +
                            $"the game stopped changing too. {action.Reason}");
                        yield return Screenshot(report, "dead-end");
                        Finish(report, BotOutcome.Stuck, "The bot could not find a way forward.");
                        break;
                    }
                    continue;
                }

                string problem = null;
                yield return Perform(action, failure => problem = failure);

                if (bot.ThinkTime > 0f) yield return Settle(bot.ThinkTime);

                var fingerprint = Fingerprint(tracked);
                bool changed = fingerprint != previousFingerprint;
                previousFingerprint = fingerprint;
                loops.Record(fingerprint);

                step.ChangedSomething = changed;
                step.Problem = problem;
                report.Steps.Add(step);

                context.ActionCount++;
                context.LastAction = action;
                context.LastActionChangedSomething = changed;
                // A patient wait counts as progress: the bot is watching something on purpose, and the
                // screen holding still is the expected outcome, not evidence of a dead end.
                context.BarrenActions = changed || action.Patient ? 0 : context.BarrenActions + 1;

                if (problem != null)
                {
                    // One miss is the board moving under the pointer, which is ordinary; the bot re-decides
                    // next beat. A streak of them is the finding — something is covering the UI, or nothing
                    // the bot can see is actually usable.
                    unreachableStreak++;
                    if (unreachableStreak >= _options.UnreachableStreakBeforeFinding)
                    {
                        Raise(report, context,
                            $"{unreachableStreak} actions in a row could not be performed. " +
                            $"Last: '{action.Describe()}' — {problem}");
                        yield return Screenshot(report, "unreachable");
                        unreachableStreak = 0;
                    }
                }
                else
                {
                    unreachableStreak = 0;
                }

                if (context.BarrenActions >= _options.StuckAfterBarrenActions)
                {
                    Raise(report, context,
                        $"Dead end: {context.BarrenActions} actions in a row changed nothing. " +
                        $"Last was '{action.Describe()}'.");
                    yield return Screenshot(report, "dead-end");
                    Finish(report, BotOutcome.Stuck, "The bot's actions stopped having any effect.");
                    break;
                }
            }

            Time.timeScale = originalTimeScale;
            _ctx.Options.InputSpeedScale = originalInputSpeed;

            report.ActionsTaken = context.ActionCount;
            report.DurationSeconds = context.Elapsed;
            bot.OnRunFinished(context, report);

            _ctx.Log($"[{bot.BotName}] {report.Outcome} — {report.Summary}");
            onFinished?.Invoke(report);
        }

        // ---------------------------------------------------------------- performing

        /// <summary>
        /// Turns an intent into real input. Every branch goes through the virtual user.
        /// </summary>
        /// <remarks>
        /// Targets resolve with <see cref="LocatorEngine.Requirement.Clickable"/>, so an element that is
        /// present but covered is reported as covered rather than silently missed — the difference between
        /// "the bot got stuck" and "a popup was over the button".
        /// </remarks>
        private IEnumerator Perform(BotAction action, Action<string> onProblem)
        {
            Vector2 from = default, to = default;

            if (action.Kind != BotActionKind.Wait && action.Kind != BotActionKind.Press)
            {
                if (action.Point.HasValue) from = action.Point.Value;
                else
                {
                    var resolved = new ResolvedTarget();
                    bool failed = false;
                    yield return Guarded(
                        _ctx.Locate.ResolvePoint(action.Target, resolved, LocatorEngine.Requirement.Clickable,
                            _options.TargetTimeoutSeconds),
                        message => { onProblem(message); failed = true; });
                    if (failed) yield break;
                    from = resolved.ScreenPoint;
                }
            }

            if (action.Kind == BotActionKind.Drag || action.Kind == BotActionKind.Swipe)
            {
                if (action.SecondPoint.HasValue) to = action.SecondPoint.Value;
                else
                {
                    var resolved = new ResolvedTarget();
                    bool failed = false;
                    yield return Guarded(
                        _ctx.Locate.ResolvePoint(action.SecondTarget, resolved, LocatorEngine.Requirement.Visible,
                            _options.TargetTimeoutSeconds),
                        message => { onProblem(message); failed = true; });
                    if (failed) yield break;
                    to = resolved.ScreenPoint;
                }
            }

            switch (action.Kind)
            {
                case BotActionKind.Wait:
                    yield return Settle(action.Seconds);
                    break;
                case BotActionKind.Tap:
                    yield return _ctx.User.Click(from);
                    break;
                case BotActionKind.DoubleTap:
                    yield return _ctx.User.Click(from, clicks: 2);
                    break;
                case BotActionKind.Hold:
                    yield return _ctx.User.Hold(from, action.Seconds);
                    break;
                case BotActionKind.Drag:
                case BotActionKind.Swipe:
                    yield return _ctx.User.Drag(from, to);
                    break;
                case BotActionKind.Press:
                    yield return Guarded(_ctx.User.PressKey(action.Text), onProblem);
                    break;
                case BotActionKind.Type:
                    yield return _ctx.User.Click(from);
                    yield return _ctx.User.TypeText(action.Text);
                    break;
            }
        }

        /// <summary>
        /// Runs a routine to completion, turning a test failure at any depth into a reported problem.
        /// </summary>
        /// <remarks>
        /// It drives nested enumerators itself rather than yielding them onward, and that detail is the
        /// whole point. A coroutine runner that receives an <see cref="IEnumerator"/> from a yield will
        /// push and drive it on its own stack — so a naive wrapper's try/catch only ever guards the
        /// outermost frame, and an exception thrown one level down (the locator giving up on a target,
        /// say) sails straight past it. Flattening the stack here is what makes the catch actually cover
        /// the call.
        /// <para>
        /// A bot hitting an unreachable element is information, not a crash: the run carries on and sees
        /// whether it recovers, because "recovers by itself" and "wedged forever" are very different bugs.
        /// </para>
        /// </remarks>
        private static IEnumerator Guarded(IEnumerator routine, Action<string> onProblem)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);

            while (stack.Count > 0)
            {
                var top = stack.Peek();
                bool moved = false;
                bool failed = false;
                object current = null;

                try
                {
                    moved = top.MoveNext();
                    if (moved) current = top.Current;
                }
                catch (TestFailureException e)
                {
                    onProblem(e.Message);
                    failed = true;
                }

                if (failed) yield break;
                if (!moved) { stack.Pop(); continue; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }

                yield return current;
            }
        }

        // ---------------------------------------------------------------- helpers

        private static IEnumerator Settle(float seconds)
        {
            float until = Time.realtimeSinceStartup + Mathf.Max(0f, seconds);
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        private IEnumerator Screenshot(BotRunReport report, string name)
        {
            if (!_options.ScreenshotOnFinding || _ctx.Artifacts == null) yield break;
            yield return _ctx.Artifacts.Screenshot($"bot-{name}-{report.Steps.Count}", path =>
            {
                if (path != null) report.Screenshots.Add(path);
            });
        }

        /// <summary>
        /// Everything the bot can sense, as one comparable string.
        /// </summary>
        /// <remarks>
        /// Tracked bindings plus the set of interactable selectors. Both halves matter: a game can change
        /// its numbers without changing the screen (a timer ticking) and change the screen without changing
        /// its numbers (a popup opening), and either counts as the world moving.
        /// </remarks>
        private static string Fingerprint(List<string> tracked)
        {
            var sb = new StringBuilder();
            foreach (var path in tracked)
                sb.Append(path).Append('=')
                  .Append(Describe(GameTestBindings.TryGetValue(path, out var value) ? value : null)).Append(';');

            var snapshot = SceneInspector.Capture(120, includeHidden: false);
            sb.Append(snapshot.ActiveScene).Append('|');
            foreach (var element in snapshot.Interactables)
                if (element.Interactable) sb.Append(element.Selector).Append(',');

            return sb.ToString();
        }

        private static string Describe(object value) => value?.ToString() ?? "null";

        /// <summary>
        /// The distinct actions of the last few steps, in the order they came round.
        /// </summary>
        /// <remarks>
        /// The state fingerprint is what detects the circle, but it is unreadable — a list of selectors.
        /// What makes the finding actionable is the two or three actions the bot kept alternating
        /// between, because that is the flow to go and look at.
        /// </remarks>
        private static string DescribeCycle(BotRunReport report, int beats)
        {
            var seen = new List<string>();
            for (int i = Math.Max(0, report.Steps.Count - beats); i < report.Steps.Count; i++)
            {
                var action = report.Steps[i].Action;
                if (!string.IsNullOrEmpty(action) && !seen.Contains(action)) seen.Add(action);
            }
            return seen.Count == 0 ? "" : "It kept doing: " + string.Join(" → ", seen.ToArray()) + ".";
        }

        private static void Raise(BotRunReport report, BotContext context, string finding)
        {
            report.Findings.Add($"[action {context.ActionCount}] {finding}");
            Debug.LogWarning($"[GameTestKit] bot finding: {finding}");
        }

        private static void Finish(BotRunReport report, BotOutcome outcome, string summary)
        {
            report.Outcome = outcome;
            report.Summary = summary;
        }
    }
}
