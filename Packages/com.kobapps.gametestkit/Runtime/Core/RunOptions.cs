using System;
using System.Collections.Generic;

namespace Kobapps.GameTestKit
{
    /// <summary>Which input pipeline the virtual user drives.</summary>
    public enum InputBackendKind
    {
        /// <summary>Device-level Input System events when available, uGUI event injection otherwise. Recommended.</summary>
        Auto = 0,
        /// <summary>Force device-level Input System injection (real devices, full pipeline). Requires the new input backend.</summary>
        InputSystem = 1,
        /// <summary>Force uGUI EventSystem injection. UI only, but works with the legacy Input Manager.</summary>
        EventSystem = 2,
    }

    /// <summary>Whether a "click" is delivered as a mouse press or as a finger touch.</summary>
    public enum PointerMode
    {
        Mouse = 0,
        Touch = 1,
    }

    /// <summary>Everything that varies between runs. Serializable so the Editor can hand it to play mode.</summary>
    [Serializable]
    public sealed class RunOptions
    {
        /// <summary>Substring match on the test name. Empty runs everything.</summary>
        public string NameFilter = "";

        /// <summary>Only run tests carrying at least one of these tags. Empty means all tags.</summary>
        public List<string> Tags = new List<string>();

        /// <summary>Tests carrying any of these tags are skipped.</summary>
        public List<string> ExcludeTags = new List<string>();

        /// <summary>
        /// Only run tests in these categories. A parent includes everything under it, so
        /// <c>Shop</c> also runs <c>Shop/Checkout</c>. Empty means every category.
        /// </summary>
        public List<string> Categories = new List<string>();

        /// <summary>Tests in these categories (or nested under them) are skipped.</summary>
        public List<string> ExcludeCategories = new List<string>();

        /// <summary>Explicit script paths to run. When non-empty, discovery is skipped.</summary>
        public List<string> Paths = new List<string>();

        /// <summary>Run the whole selection this many times.</summary>
        public int RunRepeat = 1;

        /// <summary>Re-run a failing test up to this many times before reporting it (flake tolerance).</summary>
        public int Retries;

        /// <summary>Abort the batch as soon as one test fails.</summary>
        public bool StopOnFirstFailure;

        /// <summary>Shuffle test order to surface order-dependence. Uses <see cref="Seed"/>.</summary>
        public bool Shuffle;

        /// <summary>Seed for shuffling and for any <c>random</c> step. 0 picks a fresh seed and reports it.</summary>
        public int Seed;

        /// <summary>Wall-clock budget applied to steps that don't set their own.</summary>
        public float DefaultStepTimeout = 15f;

        /// <summary>Implicit wait applied when resolving a locator before giving up (Playwright-style auto-wait).</summary>
        public float LocatorTimeout = 5f;

        /// <summary>Global multiplier on gesture durations. &gt;1 slows input down so you can watch it.</summary>
        public float InputSpeedScale = 1f;

        /// <summary>Time.timeScale applied for the run. 0 leaves it alone. Values &gt;1 fast-forward the game.</summary>
        public float TimeScale;

        /// <summary>Mouse press vs. finger touch for pointer gestures.</summary>
        public PointerMode Pointer = PointerMode.Mouse;

        public InputBackendKind Backend = InputBackendKind.Auto;

        /// <summary>Disable real hardware devices while the run is in progress so a stray human input can't corrupt it.</summary>
        public bool IsolateRealDevices;

        /// <summary>
        /// Draw the on-screen overlay — yellow markers where the virtual user is interacting, and a
        /// caption strip with the current step and the text being typed. On by default.
        /// </summary>
        /// <remarks>
        /// Costs nothing worth measuring, and appears in failure screenshots, which is usually what makes
        /// one readable. Turn it off for a run whose screenshots are compared against reference images.
        /// </remarks>
        public bool ShowInputOverlay = true;

        /// <summary>Any <c>LogType.Error</c>/<c>Exception</c> from the game fails the running test.</summary>
        public bool FailOnLogError = true;

        /// <summary>Regex patterns for log messages that <see cref="FailOnLogError"/> should ignore.</summary>
        public List<string> IgnoredLogPatterns = new List<string>();

        /// <summary>Capture a screenshot when a test fails.</summary>
        public bool ScreenshotOnFailure = true;

        /// <summary>Capture a screenshot after every step. Slow — for debugging a specific test.</summary>
        public bool ScreenshotEveryStep;

        /// <summary>Collect frame-time and memory statistics per test.</summary>
        public bool CollectPerformance = true;

        /// <summary>Absolute directory for reports and screenshots. Empty resolves to <c>&lt;project&gt;/GameTestKit</c>.</summary>
        public string ArtifactRoot = "";

        /// <summary>Report formats to write: <c>junit</c>, <c>json</c>, <c>html</c>.</summary>
        public List<string> ReportFormats = new List<string> { "junit", "json", "html" };

        /// <summary>Quit the player/editor when the run finishes (CI).</summary>
        public bool QuitWhenDone;

        /// <summary>
        /// Steps run before every test in the batch, as the JSON array a suite or the settings asset
        /// declares them in. Empty for none.
        /// </summary>
        /// <remarks>
        /// Held as text rather than as parsed <see cref="TestStep"/>s because these options are
        /// serialised to a file and handed to play mode across a domain reload, and steps are authored
        /// as JSON anyway. The runner parses them once, at the start of the run, so a broken fixture is
        /// one error rather than one per test.
        /// <para>
        /// A failure here is reported like a failed <c>setup</c> — an error, not a failure. The test
        /// never got to run, and calling that a product bug wastes somebody's morning.
        /// </para>
        /// </remarks>
        public string BeforeEachJson = "";

        /// <summary>Steps run after every test, pass or fail. Failures are logged, not fatal — as with teardown.</summary>
        public string AfterEachJson = "";

        public RunOptions Clone()
        {
            return new RunOptions
            {
                NameFilter = NameFilter,
                Tags = new List<string>(Tags),
                ExcludeTags = new List<string>(ExcludeTags),
                Categories = new List<string>(Categories),
                ExcludeCategories = new List<string>(ExcludeCategories),
                Paths = new List<string>(Paths),
                RunRepeat = RunRepeat,
                Retries = Retries,
                StopOnFirstFailure = StopOnFirstFailure,
                Shuffle = Shuffle,
                Seed = Seed,
                DefaultStepTimeout = DefaultStepTimeout,
                LocatorTimeout = LocatorTimeout,
                InputSpeedScale = InputSpeedScale,
                TimeScale = TimeScale,
                Pointer = Pointer,
                Backend = Backend,
                IsolateRealDevices = IsolateRealDevices,
                ShowInputOverlay = ShowInputOverlay,
                FailOnLogError = FailOnLogError,
                IgnoredLogPatterns = new List<string>(IgnoredLogPatterns),
                ScreenshotOnFailure = ScreenshotOnFailure,
                ScreenshotEveryStep = ScreenshotEveryStep,
                CollectPerformance = CollectPerformance,
                ArtifactRoot = ArtifactRoot,
                ReportFormats = new List<string>(ReportFormats),
                QuitWhenDone = QuitWhenDone,
                BeforeEachJson = BeforeEachJson,
                AfterEachJson = AfterEachJson,
            };
        }

        /// <summary>True when <paramref name="test"/> passes the name/tag filters.</summary>
        public bool Matches(GameTest test)
        {
            if (test == null) return false;

            if (!string.IsNullOrEmpty(NameFilter) &&
                test.Name.IndexOf(NameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (ExcludeTags != null)
                for (int i = 0; i < ExcludeTags.Count; i++)
                    if (test.HasTag(ExcludeTags[i])) return false;

            if (ExcludeCategories != null)
                for (int i = 0; i < ExcludeCategories.Count; i++)
                    if (test.IsInCategory(ExcludeCategories[i])) return false;

            // Categories and tags are separate axes, so both have to say yes: "the Shop folder" and
            // "tagged smoke" together means the smoke tests in Shop, not their union.
            if (Categories != null && Categories.Count > 0)
            {
                bool inAny = false;
                for (int i = 0; i < Categories.Count && !inAny; i++)
                    inAny = test.IsInCategory(Categories[i]);
                if (!inAny) return false;
            }

            if (Tags != null && Tags.Count > 0)
            {
                for (int i = 0; i < Tags.Count; i++)
                    if (test.HasTag(Tags[i])) return true;
                return false;
            }

            return true;
        }
    }
}
