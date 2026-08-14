using System;
using System.Collections;
using System.Collections.Generic;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// One end-to-end test: an ordered list of steps that drive the real game with real input.
    /// Built either from a <c>.gametest.json</c> script (see <see cref="Scripting.TestScriptParser"/>)
    /// or from the C# fluent API (see <see cref="Test"/>).
    /// </summary>
    public sealed class GameTest
    {
        /// <summary>Unique, human-readable name. Used for filtering, reporting and artifact paths.</summary>
        public string Name = "Untitled test";

        /// <summary>Free-text description shown in reports and the GameTester window.</summary>
        public string Description;

        /// <summary>Where this test came from (asset path or file path). Empty for tests built in code.</summary>
        public string SourcePath;

        /// <summary>
        /// Where this test sits in the suite, as a <c>/</c>-separated path such as <c>Shop/Checkout</c>.
        /// Empty means "no category". Normally derived from the folder the script lives in; a script can
        /// override it with a <c>"category"</c> field. See <see cref="TestCategory"/>.
        /// </summary>
        public string Category = "";

        /// <summary>Tags used by suite filters, e.g. <c>smoke</c>, <c>shop</c>, <c>nightly</c>.</summary>
        /// <remarks>
        /// Orthogonal to <see cref="Category"/>: a test lives in exactly one category (its folder) but
        /// carries any number of tags, so <c>smoke</c> can cut across the whole tree.
        /// </remarks>
        public readonly List<string> Tags = new List<string>();

        /// <summary>Scene loaded before <see cref="Setup"/> runs. Null keeps the current scene.</summary>
        public string Scene;

        /// <summary>Wall-clock budget for the whole test (setup + steps + teardown). Unscaled seconds.</summary>
        public float TimeoutSeconds = 120f;

        /// <summary>Steps run before <see cref="Steps"/>. Failures here mark the test as errored, not failed.</summary>
        public readonly List<TestStep> Setup = new List<TestStep>();

        /// <summary>The test body.</summary>
        public readonly List<TestStep> Steps = new List<TestStep>();

        /// <summary>Steps that always run, even after a failure. Best-effort: their failures are logged, not fatal.</summary>
        public readonly List<TestStep> Teardown = new List<TestStep>();

        /// <summary>Run the body this many times inside a single test (soak / flake hunting).</summary>
        public int Repeat = 1;

        /// <summary>How many times to re-run the whole test after a failure before reporting it.</summary>
        public int Retries;

        /// <summary>When set, the test is reported as skipped with <see cref="SkipReason"/>.</summary>
        public bool Skip;

        public string SkipReason;

        public GameTest() { }

        public GameTest(string name) { Name = name; }

        public bool HasTag(string tag)
        {
            for (int i = 0; i < Tags.Count; i++)
                if (string.Equals(Tags[i], tag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>True when this test is in <paramref name="category"/> or in one nested under it.</summary>
        public bool IsInCategory(string category) => TestCategory.IsWithin(Category, category);

        /// <summary>The category as shown to a human — "Uncategorized" when there is none.</summary>
        public string CategoryLabel => TestCategory.Display(Category);

        /// <summary>Category and name together, unique across the suite: <c>Shop/Checkout ▸ Buy a sword</c>.</summary>
        public string FullName =>
            string.IsNullOrEmpty(Category) ? Name : $"{Category} ▸ {Name}";

        public override string ToString() => Name;
    }

    /// <summary>
    /// A single action or assertion. Steps are executed by <see cref="TestRunner"/>, which drives the
    /// returned enumerator itself so it can apply timeouts and catch exceptions across yields.
    /// </summary>
    /// <remarks>
    /// A step reports failure by throwing <see cref="TestFailureException"/> (usually via
    /// <see cref="TestContext.Fail"/>). Any other exception is reported as an error.
    /// Yield <c>null</c> to wait a frame, an <see cref="IEnumerator"/> to run it inline (its exceptions
    /// propagate), or any Unity yield instruction (<c>WaitForSeconds</c>, <c>AsyncOperation</c>, …).
    /// </remarks>
    public abstract class TestStep
    {
        /// <summary>Optional label overriding <see cref="Describe"/> in reports.</summary>
        public string Label;

        /// <summary>Per-step wall-clock budget in unscaled seconds. Null uses <see cref="RunOptions.DefaultStepTimeout"/>.</summary>
        public float? TimeoutSeconds;

        /// <summary>When true a failure is recorded but the test keeps going (soft assertion).</summary>
        public bool ContinueOnFailure;

        /// <summary>Short one-line description, e.g. <c>click #PlayButton</c>.</summary>
        public abstract string Describe();

        public abstract IEnumerator Execute(TestContext ctx);

        public string DisplayName => string.IsNullOrEmpty(Label) ? Describe() : Label;

        public override string ToString() => DisplayName;
    }

    /// <summary>Thrown by a step to fail the test. Carries no stack noise — the message is the report.</summary>
    public sealed class TestFailureException : Exception
    {
        public TestFailureException(string message) : base(message) { }
        public TestFailureException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Thrown when a step or a test exceeds its time budget.</summary>
    public sealed class TestTimeoutException : Exception
    {
        public TestTimeoutException(string message) : base(message) { }
    }
}
