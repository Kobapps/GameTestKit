using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Builds a <see cref="GameTest"/> in C#. Use this when a test needs real code — a loop over
    /// generated data, a custom step, a call into your own systems. For plain flows prefer a
    /// <c>.gametest.json</c> script: it needs no recompile and an agent can write it directly.
    /// </summary>
    /// <example>
    /// <code>
    /// var test = TestBuilder.Create("Buy a sword")
    ///     .Tag("smoke").Scene("Shop")
    ///     .Call("grantGold", 100)
    ///     .Click("id:shop_button")
    ///     .WaitForVisible("id:shop_panel")
    ///     .Click("text:\"Sword\"")
    ///     .AssertText("id:gold_label", equals: "40")
    ///     .Build();
    ///
    /// yield return GameTester.RunSingleAsync(test);
    /// </code>
    /// </example>
    public sealed class TestBuilder
    {
        private readonly GameTest _test;
        private List<TestStep> _target;

        private TestBuilder(string name)
        {
            _test = new GameTest(name);
            _target = _test.Steps;
        }

        public static TestBuilder Create(string name) => new TestBuilder(name);

        public GameTest Build() => _test;

        // ---------------------------------------------------------------- metadata

        public TestBuilder Describe(string description) { _test.Description = description; return this; }

        public TestBuilder Tag(params string[] tags) { _test.Tags.AddRange(tags); return this; }

        public TestBuilder Scene(string sceneName) { _test.Scene = sceneName; return this; }

        public TestBuilder Timeout(float seconds) { _test.TimeoutSeconds = seconds; return this; }

        public TestBuilder Retries(int count) { _test.Retries = count; return this; }

        public TestBuilder RepeatBody(int times) { _test.Repeat = times; return this; }

        public TestBuilder Skip(string reason) { _test.Skip = true; _test.SkipReason = reason; return this; }

        /// <summary>Steps that prepare the world. A failure here reports as an error, not a test failure.</summary>
        public TestBuilder Setup(Action<TestBuilder> build) => Section(_test.Setup, build);

        /// <summary>Steps that always run, even after a failure.</summary>
        public TestBuilder Teardown(Action<TestBuilder> build) => Section(_test.Teardown, build);

        private TestBuilder Section(List<TestStep> list, Action<TestBuilder> build)
        {
            var previous = _target;
            _target = list;
            build?.Invoke(this);
            _target = previous;
            return this;
        }

        // ---------------------------------------------------------------- steps

        /// <summary>Appends any step, including one of your own types.</summary>
        public TestBuilder Step(TestStep step)
        {
            if (step != null) _target.Add(step);
            return this;
        }

        public TestBuilder Click(string selector, PointerButton button = PointerButton.Left, int clicks = 1) =>
            Step(new ClickStep { Selector = selector, Button = button, Clicks = clicks });

        public TestBuilder ClickAt(string selector, Vector2 normalizedPoint) =>
            Step(new ClickStep { Selector = selector, NormalizedAt = normalizedPoint });

        public TestBuilder DoubleClick(string selector) =>
            Step(new ClickStep { Selector = selector, Clicks = 2 });

        public TestBuilder Hold(string selector, float seconds) =>
            Step(new HoldStep { Selector = selector, Seconds = seconds });

        public TestBuilder Hover(string selector) => Step(new MoveStep { Selector = selector });

        public TestBuilder Drag(string from, string to, float duration = 0.35f) =>
            Step(new DragStep { FromSelector = from, ToSelector = to, Duration = duration });

        public TestBuilder Swipe(string direction, string from = null, float distance = 0f) =>
            Step(new SwipeStep { Direction = direction, FromSelector = from, Distance = distance });

        public TestBuilder Scroll(float notches, string over = null) =>
            Step(new ScrollStep { Amount = notches, Selector = over });

        public TestBuilder Type(string text, string into = null, bool clear = false) =>
            Step(new TypeTextStep { Text = text, IntoSelector = into, Clear = clear });

        public TestBuilder Press(string key, int repeat = 1) =>
            Step(new PressKeyStep { Key = key, Repeat = repeat });

        public TestBuilder KeyDown(string key) => Step(new KeyHoldStep { Key = key, Down = true });

        public TestBuilder KeyUp(string key) => Step(new KeyHoldStep { Key = key, Down = false });

        public TestBuilder Gamepad(string button, float hold = 0.08f) =>
            Step(new GamepadButtonStep { Button = button, HoldSeconds = hold });

        public TestBuilder Stick(string stick, Vector2 value, float seconds) =>
            Step(new GamepadStickStep { Stick = stick, Value = value, Seconds = seconds });

        public TestBuilder Wait(float seconds) => Step(new WaitStep { Seconds = seconds });

        public TestBuilder WaitFor(string expression, float timeout = 10f) =>
            Step(new WaitForStep { Expression = expression, Timeout = timeout });

        public TestBuilder WaitForVisible(string selector, float timeout = 10f) =>
            Step(new WaitForElementStep { Selector = selector, Timeout = timeout });

        public TestBuilder WaitForGone(string selector, float timeout = 10f) =>
            Step(new WaitForElementStep { Selector = selector, Gone = true, Timeout = timeout });

        public TestBuilder WaitForScene(string sceneName, float timeout = 30f) =>
            Step(new WaitForSceneStep { SceneName = sceneName, Timeout = timeout });

        public TestBuilder LoadScene(string sceneName, bool additive = false) =>
            Step(new LoadSceneStep { SceneName = sceneName, Additive = additive });

        public TestBuilder UnloadScene(string sceneName) => Step(new UnloadSceneStep { SceneName = sceneName });

        public TestBuilder Call(string action, params object[] args) =>
            Step(new CallStep { Action = action, Args = args });

        public TestBuilder TimeScale(float scale) => Step(new TimeScaleStep { Scale = scale });

        public TestBuilder Screenshot(string name) => Step(new ScreenshotStep { Name = name });

        public TestBuilder Log(string message) => Step(new LogStep { Message = message });

        public TestBuilder Assert(string expression, string message = null, float retryFor = 0f) =>
            Step(new AssertStep { Expression = expression, Message = message, RetryFor = retryFor });

        public TestBuilder AssertVisible(string selector) =>
            Step(new AssertElementStep { Selector = selector, Condition = ElementCondition.Visible });

        public TestBuilder AssertHidden(string selector) =>
            Step(new AssertElementStep { Selector = selector, Condition = ElementCondition.Hidden });

        public TestBuilder AssertInteractable(string selector) =>
            Step(new AssertElementStep { Selector = selector, Condition = ElementCondition.Interactable });

        public TestBuilder AssertDisabled(string selector) =>
            Step(new AssertElementStep { Selector = selector, Condition = ElementCondition.Disabled });

        public TestBuilder AssertText(string selector, string equals = null, string contains = null,
            string matches = null) =>
            Step(new AssertTextStep
            {
                Selector = selector,
                ExpectedEquals = equals,
                ExpectedContains = contains,
                ExpectedPattern = matches,
            });

        public TestBuilder ExpectLog(string pattern, float within = 5f) =>
            Step(new ExpectLogStep { Pattern = pattern, Within = within });

        /// <summary>Names a block of steps so the report reads like the flow.</summary>
        public TestBuilder Group(string name, Action<TestBuilder> build)
        {
            var group = new GroupStep { Name = name };
            return Nested(group, group.Steps, build);
        }

        /// <summary>Repeats a block of steps.</summary>
        public TestBuilder Repeat(int times, Action<TestBuilder> build)
        {
            var repeat = new RepeatStep { Times = times };
            return Nested(repeat, repeat.Steps, build);
        }

        private TestBuilder Nested(TestStep container, List<TestStep> children, Action<TestBuilder> build)
        {
            _target.Add(container);
            var previous = _target;
            _target = children;
            build?.Invoke(this);
            _target = previous;
            return this;
        }
    }
}
