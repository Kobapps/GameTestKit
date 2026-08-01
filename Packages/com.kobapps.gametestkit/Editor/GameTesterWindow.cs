using System;
using System.Collections.Generic;
using System.IO;
using EditorCoreKit.Editor;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// The control room: browse discovered tests, run one or a batch, watch results, and jump to the
    /// artifacts.
    /// </summary>
    /// <remarks>
    /// Built with EditorCoreKit on UI Toolkit, so it follows whichever theme and density the editor is
    /// set to. The window owns no run state of its own: a run happens in play mode, which wipes every
    /// static field on the way in and out, so results are read back from the report file rather than
    /// held in memory (see <see cref="EditorTestRunner"/>).
    /// </remarks>
    public sealed partial class GameTesterWindow : EditorWindow
    {
        private const string OptionsPrefKey = "GameTestKit.RunOptions";
        private const string SelectionPrefKey = "GameTestKit.Selection";
        private const string PagePrefKey = "GameTestKit.Page";

        private enum Page
        {
            Tests = 0,
            Results = 1,
            Editor = 2,
            Options = 3,
        }

        // --- data ---------------------------------------------------------------------------------

        private readonly List<GameTest> _tests = new List<GameTest>();
        private readonly List<GameTest> _visible = new List<GameTest>();
        private readonly List<string> _loadErrors = new List<string>();
        private readonly HashSet<string> _picked = new HashSet<string>(StringComparer.Ordinal);

        private RunOptions _options;
        private RunReport _report;
        private string _search = "";
        private string _tagFilter = "";
        private Page _page;
        private int _detailIndex = -1;
        private bool _wasRunning;

        // --- ui -----------------------------------------------------------------------------------

        private KUIWindowShell _shell;
        private KUIVirtualList<GameTest> _list;
        private VisualElement _detailPane;

        [MenuItem("Tools/GameTestKit/GameTester %#t", priority = 0)]
        public static GameTesterWindow Open()
        {
            var window = GetWindow<GameTesterWindow>();
            window.titleContent = new GUIContent("GameTester");
            window.minSize = new Vector2(720, 460);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            LoadOptions();
            Refresh();
            _report = EditorTestRunner.LoadLastReport();
            _page = (Page)Mathf.Clamp(EditorPrefs.GetInt(PagePrefKey, 0), 0, 3);

            EditorTestRunner.RunCompleted += OnRunCompleted;
            EditorTestRunner.RunFailedToStart += OnRunFailed;
        }

        private void OnDisable()
        {
            EditorTestRunner.RunCompleted -= OnRunCompleted;
            EditorTestRunner.RunFailedToStart -= OnRunFailed;
            SaveOptions();

            // Anything deferred past this point would run against a dead window.
            _shell = null;
        }

        private void CreateGUI()
        {
            _shell = new KUIWindowShell("GameTestKit", "GameTester").MountInto(rootVisualElement);

            BuildHeader();
            BuildSidebar();
            ShowPage(_page);

            // A run lives in play mode, so the window learns that one started or ended by watching,
            // not by being told. One timer is cheaper than repainting every frame.
            rootVisualElement.schedule.Execute(TickRunState).Every(400);
        }

        // ================================================================ chrome

        private void BuildHeader()
        {
            if (_shell == null) return;

            _shell.Header.Rebuild(controls =>
            {
                controls.Add(new KUIPill(
                    string.IsNullOrEmpty(_tagFilter) ? "All tags" : _tagFilter,
                    KUITheme.Accent,
                    anchor => BuildTagMenu().ShowUnder(anchor),
                    "Only run and show tests carrying this tag."));

                controls.Add(KUILayout.VerticalSeparator());

                var refresh = KUIButton.Secondary("Refresh", Refresh);
                refresh.tooltip = "Re-scan the project for .gametest.json scripts.";
                controls.Add(refresh);

                var create = KUIButton.Secondary("New Test", () =>
                {
                    GameTesterMenu.CreateTestScript();
                    Refresh();
                });
                create.tooltip = "Create a template script in your tests folder.";
                controls.Add(create);

                var record = KUIButton.Secondary("Record", TestRecorderWindow.Open);
                record.tooltip = "Capture a live play session as a test script.";
                controls.Add(record);

                controls.Add(KUILayout.VerticalSeparator());

                if (EditorTestRunner.IsRunning)
                {
                    var stop = KUIButton.Danger("■ Stop", () =>
                    {
                        EditorTestRunner.Cancel();
                        SetStatus("Run cancelled.", KUITone.Warning);
                        BuildHeader();
                    });
                    stop.tooltip = "Leave play mode and abandon the run.";
                    controls.Add(stop);
                    return;
                }

                var runSelected = KUIButton.Secondary($"Run Selected ({_picked.Count})",
                    () => StartRun(new List<string>(_picked)));
                runSelected.tooltip = "Run only the ticked tests.";
                runSelected.SetEnabled(_picked.Count > 0);
                controls.Add(runSelected);

                var runAll = KUIButton.Primary("▶ Run All", () => StartRun(null));
                runAll.tooltip = "Run everything that passes the current filters.";
                runAll.SetEnabled(_tests.Count > 0);
                controls.Add(runAll);
            });
        }

        private KUIMenu BuildTagMenu()
        {
            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var test in _tests)
                foreach (var tag in test.Tags)
                    tags.Add(tag);

            var menu = KUIMenu.New()
                .Item("All tags", () => SetTagFilter(""), string.IsNullOrEmpty(_tagFilter));

            if (tags.Count == 0)
                return menu.Separator().Disabled("No tags in any script yet");

            menu.Separator();

            foreach (var tag in tags)
            {
                var captured = tag;
                menu.Item(captured, () => SetTagFilter(captured),
                    string.Equals(captured, _tagFilter, StringComparison.OrdinalIgnoreCase));
            }

            return menu;
        }

        private void SetTagFilter(string tag)
        {
            _tagFilter = tag ?? "";
            BuildHeader();
            RefreshList();
        }

        private void BuildSidebar()
        {
            if (_shell?.Sidebar == null) return;

            var sidebar = _shell.Sidebar;
            sidebar.Reset();

            sidebar.AddGroup("Suite");
            sidebar.Add("Tests", () => ShowPage(Page.Tests), Count(_tests.Count));
            sidebar.Add("Results", () => ShowPage(Page.Results), Count(_report?.Tests.Count ?? 0));

            sidebar.AddSeparator();
            sidebar.Add("Script Editor", () => ShowPage(Page.Editor));
            sidebar.Add("Options", () => ShowPage(Page.Options));

            sidebar.AddSeparator();
            sidebar.AddFootnote(
                "A run enters play mode and drives the game with simulated input. Reports and failure "
                + "screenshots land in the run folder; nothing is written to your scene.");

            sidebar.SelectedIndex = (int)_page;
        }

        private static string Count(int value) => value > 0 ? value.ToString() : null;

        private void SetStatus(string message, KUITone tone = KUITone.Neutral)
        {
            _shell?.Status?.Set(message, tone);
        }

        private void UpdateStatus()
        {
            if (_shell?.Status == null) return;

            if (EditorTestRunner.IsRunning)
            {
                SetStatus("Running in play mode…", KUITone.Accent);
                return;
            }

            if (_loadErrors.Count > 0)
            {
                SetStatus($"{_loadErrors.Count} script(s) could not be parsed", KUITone.Error);
                return;
            }

            if (_report == null)
            {
                SetStatus($"{_tests.Count} test(s) discovered — nothing run yet");
                return;
            }

            _shell.Status.Set(_report.Summary(), _report.Success ? KUITone.Success : KUITone.Error);
        }

        /// <summary>Watches for play mode starting or ending so the chrome reflects it.</summary>
        private void TickRunState()
        {
            if (_shell == null) return;

            var running = EditorTestRunner.IsRunning;
            if (running == _wasRunning) return;

            _wasRunning = running;
            BuildHeader();
            UpdateStatus();
        }

        // ================================================================ pages

        private void ShowPage(Page page)
        {
            _page = page;
            EditorPrefs.SetInt(PagePrefKey, (int)page);

            if (_shell == null) return;

            _shell.Sidebar.SelectedIndex = (int)page;

            switch (page)
            {
                case Page.Results:
                    _shell.SetContent(BuildResultsPage);
                    break;
                case Page.Editor:
                    _shell.SetContent(BuildScriptEditorPage);
                    break;
                case Page.Options:
                    _shell.SetContent(BuildOptionsPage);
                    break;
                default:
                    _shell.SetContent(BuildTestsPage);
                    break;
            }

            UpdateStatus();
        }

        // --- tests --------------------------------------------------------------------------------

        private VisualElement BuildTestsPage()
        {
            var root = KUILayout.Column();
            root.style.flexGrow = 1;
            root.style.minHeight = 0;

            if (_loadErrors.Count > 0)
            {
                root.Add(new KUIBanner(
                    KUITone.Error,
                    $"{_loadErrors.Count} script(s) could not be parsed",
                    string.Join("\n", _loadErrors)));
            }

            if (_tests.Count == 0 && _loadErrors.Count == 0)
            {
                root.Add(new KUIEmptyState(
                    "No tests yet",
                    "A test is a .gametest.json file anywhere under Assets. Write one by hand, or record "
                    + "a flow by playing it once and letting the recorder write the script for you.",
                    "New Test",
                    () => { GameTesterMenu.CreateTestScript(); Refresh(); },
                    "▶"));

                root.Add(KUILayout.Row(
                    KUIButton.Secondary("Record a flow", TestRecorderWindow.Open),
                    KUIButton.Ghost("Import the demo sample", OpenPackageManager)));

                return root;
            }

            var split = new KUISplitView(320f, false, "GameTestKit.Tests");
            split.style.flexGrow = 1;
            split.style.minHeight = 0;

            // --- master ---------------------------------------------------
            var left = KUILayout.Column();
            left.style.flexGrow = 1;
            left.style.minHeight = 0;

            var toolbar = new KUIToolbar();
            toolbar.With(new KUISearchField("Filter tests…", value =>
            {
                _search = value;
                RefreshList();
            }));
            toolbar.PushRight();
            toolbar.With(KUIButton.Ghost("All", () => PickAll(true)));
            toolbar.With(KUIButton.Ghost("None", () => PickAll(false)));
            left.Add(toolbar);

            RebuildVisible();

            _list = new KUIVirtualList<GameTest>(_visible, MakeRow, BindRow, 26f)
            {
                EmptyMessage = "No test matches the current filter.",
            };
            _list.SelectionChanged += index =>
            {
                _detailIndex = index;
                ShowDetail();
            };
            left.Add(_list);

            split.First.Add(left);

            // --- detail ---------------------------------------------------
            _detailPane = KUILayout.Page();
            _detailPane.style.flexGrow = 1;
            split.Second.Add(_detailPane);

            root.Add(split);

            if (_detailIndex >= 0 && _detailIndex < _visible.Count)
                _list.SelectedIndex = _detailIndex;

            ShowDetail();
            return root;
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList(KUIClass.ListItem);

            var pick = new Toggle { name = "pick" };
            pick.style.marginLeft = 2;
            pick.style.marginRight = 4;
            pick.RegisterValueChangedCallback(evt =>
            {
                if (!(row.userData is int index) || index < 0 || index >= _visible.Count) return;

                var name = _visible[index].Name;
                if (evt.newValue) _picked.Add(name);
                else _picked.Remove(name);

                BuildHeader();
            });
            row.Add(pick);

            row.Add(new KUIStatusDot(KUITone.Neutral) { name = "dot" });

            var label = new Label { name = "label" };
            label.AddToClassList(KUIClass.ListItemLabel);
            label.style.flexGrow = 1;
            row.Add(label);

            var tags = new Label { name = "tags" };
            tags.AddToClassList(KUIClass.Muted);
            row.Add(tags);

            return row;
        }

        private void BindRow(VisualElement row, GameTest test, int index)
        {
            row.userData = index;

            var pick = row.Q<Toggle>("pick");
            pick?.SetValueWithoutNotify(_picked.Contains(test.Name));

            var dot = row.Q<KUIStatusDot>("dot");
            if (dot != null)
            {
                var record = FindRecord(test.Name);
                dot.Tone = ToneOf(record?.Status);
                dot.tooltip = record != null ? record.Status.ToString() : "Not run in the last batch";
            }

            var label = row.Q<Label>("label");
            if (label != null)
            {
                label.text = test.Name;
                label.tooltip = test.SourcePath;
            }

            var tags = row.Q<Label>("tags");
            if (tags != null)
                tags.text = test.Tags.Count > 0 ? string.Join(" ", test.Tags) : "";
        }

        private void ShowDetail()
        {
            if (_detailPane == null) return;

            _detailPane.Clear();

            if (_detailIndex < 0 || _detailIndex >= _visible.Count)
            {
                _detailPane.Add(KUIEmptyState.Line("Select a test to see what it does."));
                return;
            }

            var test = _visible[_detailIndex];
            var record = FindRecord(test.Name);

            var card = new KUICard(test.Name, test.Description);
            card.Header.Add(KUILayout.Spacer());
            card.Header.Add(KUIButton.Primary("▶ Run", () => StartRun(new List<string> { test.Name })));

            card.Add(KUIText.KeyValueTable(
                "Steps", test.Steps.Count.ToString(),
                "Setup / teardown", $"{test.Setup.Count} / {test.Teardown.Count}",
                "Scene", string.IsNullOrEmpty(test.Scene) ? "— (uses the open scene)" : test.Scene,
                "Tags", test.Tags.Count > 0 ? string.Join(", ", test.Tags) : "—",
                "Timeout", $"{test.TimeoutSeconds:0.#}s",
                "Retries", test.Retries.ToString()));

            if (!string.IsNullOrEmpty(test.SourcePath))
            {
                card.Add(KUILayout.Row(
                    KUIText.Link(test.SourcePath, () => OpenScript(test.SourcePath)),
                    KUILayout.Spacer()));
            }

            _detailPane.Add(card);

            // The steps, as written. Reading them here beats opening the file to remember what a test
            // does before running it.
            var steps = new KUISection("Steps", true, "GameTestKit.Detail.Steps");
            AppendStepList(steps, test.Setup, "setup");
            AppendStepList(steps, test.Steps, null);
            AppendStepList(steps, test.Teardown, "teardown");
            _detailPane.Add(steps);

            if (record != null)
                _detailPane.Add(BuildResultCard(record, expanded: true));
        }

        private static void AppendStepList(VisualElement parent, List<TestStep> steps, string phase)
        {
            foreach (var step in steps)
            {
                var line = KUILayout.Row();

                if (!string.IsNullOrEmpty(phase))
                    line.Add(new KUIBadge(phase, KUITone.Neutral));

                line.Add(KUIText.Body(step.DisplayName));
                parent.Add(line);
            }
        }

        // --- results ------------------------------------------------------------------------------

        private VisualElement BuildResultsPage()
        {
            var page = KUILayout.Page();

            if (_report == null || _report.Tests.Count == 0)
            {
                page.Add(new KUIEmptyState(
                    "No results yet",
                    "Run a test and its result lands here: every step with its own status and duration, "
                    + "the message explaining any failure, and a screenshot of the moment it happened.",
                    _tests.Count > 0 ? "Run All" : null,
                    _tests.Count > 0 ? (Action)(() => StartRun(null)) : null,
                    "◷"));
                return page;
            }

            var banner = new KUIBanner(
                _report.Success ? KUITone.Success : KUITone.Error,
                _report.Success ? "All tests passed" : "Some tests did not pass",
                $"{_report.Summary()}   ·   seed {_report.Seed}   ·   {_report.Platform}");

            var folder = EditorTestRunner.LastRunFolder();
            if (!string.IsNullOrEmpty(folder))
                banner.WithAction("Open folder", () => EditorUtility.RevealInFinder(folder));

            page.Add(banner);

            page.Add(KUILayout.Row(
                KUIButton.Secondary("HTML report", OpenHtmlReport),
                KUIButton.Ghost("Re-run failures", RunFailures)));

            foreach (var record in _report.Tests)
                page.Add(BuildResultCard(record, record.IsFailure));

            return page;
        }

        private VisualElement BuildResultCard(TestRecord record, bool expanded)
        {
            var summary = $"{record.Status} · {record.DurationSeconds:0.00}s"
                          + (record.Attempt > 1 ? $" · attempt {record.Attempt}" : "");

            var card = new KUIExpandableCard(record.Name, summary, expanded);
            card.Header.Add(new KUIBadge(record.Status.ToString(), ToneOf(record.Status)));

            if (!string.IsNullOrEmpty(record.Message))
                card.Add(new KUIBanner(ToneOf(record.Status), record.Message));

            var console = new KUILogConsole(false);
            var entries = new List<KUILogEntry>();
            AppendStepEntries(entries, record.Steps, 0);
            console.SetEntries(entries);
            console.style.maxHeight = 260;
            card.Add(console);

            foreach (var artifact in record.Artifacts)
            {
                if (!File.Exists(artifact)) continue;
                var shot = artifact;
                card.Add(KUIButton.Ghost(Path.GetFileName(shot), () => EditorUtility.RevealInFinder(shot)));
            }

            if (record.Performance != null)
            {
                card.Add(KUIText.Muted(
                    $"avg {record.Performance.AverageFps:0} fps · worst frame "
                    + $"{record.Performance.WorstFrameMs:0.0} ms · p95 {record.Performance.Percentile95FrameMs:0.0} ms"));
            }

            return card;
        }

        private static void AppendStepEntries(List<KUILogEntry> entries, List<StepRecord> steps, int depth)
        {
            var indent = new string(' ', depth * 3);

            foreach (var step in steps)
            {
                var tone = step.IsFailure ? KUITone.Error : KUITone.Neutral;
                entries.Add(new KUILogEntry(
                    $"{indent}{(step.IsFailure ? "✗" : "•")} {step.Description}",
                    tone,
                    $"{step.DurationSeconds:0.00}s"));

                if (step.IsFailure && !string.IsNullOrEmpty(step.Message))
                    entries.Add(new KUILogEntry($"{indent}   {step.Message}", KUITone.Error));

                AppendStepEntries(entries, step.Children, depth + 1);
            }
        }

        // --- options ------------------------------------------------------------------------------

        private VisualElement BuildOptionsPage()
        {
            var page = KUILayout.Page();

            var input = new KUICard("Input",
                "How the virtual user drives the game. These apply to runs started from this window.");

            input.Add(LabelledRow("Pointer", new KUISegmentedControl(
                new[] { "Mouse", "Touch" }, (int)_options.Pointer,
                index => { _options.Pointer = (PointerMode)index; SaveOptions(); })));

            input.Add(LabelledRow("Backend", new KUISegmentedControl(
                new[] { "Auto", "Input System", "EventSystem" }, (int)_options.Backend,
                index => { _options.Backend = (InputBackendKind)index; SaveOptions(); })));

            input.Add(new KUISlider("Input speed", 0.1f, 5f, _options.InputSpeedScale,
                value => { _options.InputSpeedScale = value; SaveOptions(); }, true)
                .Tip("Multiplies gesture durations. Raise it to watch what a test is doing."));

            input.Add(new KUISlider("Time scale (0 = leave alone)", 0f, 8f, _options.TimeScale,
                value => { _options.TimeScale = value; SaveOptions(); }, true)
                .Tip("Fast-forward the game itself. Waits that use game time shorten with it."));

            input.Add(new KUIToggleSwitch("Disable real input devices during a run",
                _options.IsolateRealDevices,
                value => { _options.IsolateRealDevices = value; SaveOptions(); })
                .Tip("Stops a stray mouse move or keypress from corrupting a run. Leave off while debugging."));

            input.Add(new KUIToggleSwitch("Show the input overlay", _options.ShowInputOverlay,
                value => { _options.ShowInputOverlay = value; SaveOptions(); })
                .Tip("Yellow markers where the virtual user taps, drags and scrolls, plus a caption strip "
                     + "with the current step and the text being typed. F9 hides it mid-run. It is part of "
                     + "the frame, so it also appears in failure screenshots."));

            page.Add(input);

            var run = new KUICard("Run", "Batch behaviour: order, repetition and flake tolerance.");

            run.Add(new KUISlider("Retries after a failure", 0f, 5f, _options.Retries,
                value => { _options.Retries = Mathf.RoundToInt(value); SaveOptions(); }, true));

            run.Add(new KUISlider("Repeat the whole run", 1f, 10f, Mathf.Max(1, _options.RunRepeat),
                value => { _options.RunRepeat = Mathf.Max(1, Mathf.RoundToInt(value)); SaveOptions(); }, true));

            run.Add(new KUIToggleSwitch("Stop on first failure", _options.StopOnFirstFailure,
                value => { _options.StopOnFirstFailure = value; SaveOptions(); }));

            run.Add(new KUIToggleSwitch("Shuffle test order", _options.Shuffle,
                value => { _options.Shuffle = value; SaveOptions(); })
                .Tip("Surfaces order-dependence between tests. The seed below reproduces an order exactly."));

            var seed = new TextField("Seed") { value = _options.Seed.ToString() };
            seed.RegisterValueChangedCallback(evt =>
            {
                _options.Seed = int.TryParse(evt.newValue, out var parsed) ? parsed : 0;
                SaveOptions();
            });
            seed.tooltip = "0 picks a fresh seed each run and reports it.";
            run.Add(seed);

            page.Add(run);

            var failure = new KUICard("Failure policy", "What counts as a failure, and what gets captured.");

            failure.Add(new KUIToggleSwitch("A logged error fails the test", _options.FailOnLogError,
                value => { _options.FailOnLogError = value; SaveOptions(); })
                .Tip("A Debug.LogError during a step is a defect even when every assertion passed. "
                     + "Use the expectLog step for error paths a test exercises deliberately."));

            failure.Add(new KUIToggleSwitch("Screenshot on failure", _options.ScreenshotOnFailure,
                value => { _options.ScreenshotOnFailure = value; SaveOptions(); }));

            failure.Add(new KUIToggleSwitch("Screenshot after every step", _options.ScreenshotEveryStep,
                value => { _options.ScreenshotEveryStep = value; SaveOptions(); })
                .Tip("Slow. Worth it for one stubborn test."));

            failure.Add(KUIText.Muted(
                "Screenshots need a presented frame, so they are skipped in -batchmode. Runs from this "
                + "window capture normally."));

            page.Add(failure);

            var artifacts = new KUICard("Artifacts", "Where reports and screenshots are written.");
            var folder = EditorTestRunner.LastRunFolder();

            artifacts.Add(KUIText.KeyValue("Last run", string.IsNullOrEmpty(folder) ? "— nothing run yet" : folder));
            artifacts.Add(KUILayout.Row(
                KUIButton.Secondary("Open folder", () =>
                {
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) EditorUtility.RevealInFinder(folder);
                    else KUIToast.Error(rootVisualElement, "Nothing has been run yet.");
                }),
                KUIButton.Secondary("HTML report", OpenHtmlReport),
                KUIButton.Ghost("Project settings asset", GameTesterMenu.OpenSettings)));

            artifacts.Add(KUIText.Muted(
                "These options apply to this window only. Project-wide defaults — including the folders "
                + "scanned for scripts — live on the settings asset."));

            page.Add(artifacts);

            return page;
        }

        /// <summary>A label beside a control, for controls that do not carry their own.</summary>
        private static VisualElement LabelledRow(string label, VisualElement control)
        {
            var row = KUILayout.Row();
            var text = KUIText.Muted(label);
            text.style.width = 110;
            row.Add(text);
            row.Add(control);
            return row;
        }

        // ================================================================ data

        private void Refresh()
        {
            _tests.Clear();
            _loadErrors.Clear();
            _tests.AddRange(GameTestCatalog.DiscoverTests(new RunOptions(), _loadErrors));

            var stored = EditorPrefs.GetString(SelectionPrefKey, "");
            if (_picked.Count == 0 && !string.IsNullOrEmpty(stored))
                foreach (var name in stored.Split('\n'))
                    if (!string.IsNullOrEmpty(name)) _picked.Add(name);

            // A test that no longer exists must not keep counting towards "Run Selected".
            _picked.RemoveWhere(name => !_tests.Exists(test => test.Name == name));

            if (_shell == null) return;

            BuildHeader();
            BuildSidebar();
            ShowPage(_page);
        }

        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var test in _tests)
            {
                if (!string.IsNullOrEmpty(_search) &&
                    test.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!string.IsNullOrEmpty(_tagFilter) && !test.HasTag(_tagFilter))
                    continue;

                _visible.Add(test);
            }
        }

        private void RefreshList()
        {
            RebuildVisible();

            if (_list == null) return;

            _list.SetItems(_visible);
            _detailIndex = -1;
            ShowDetail();
        }

        private void PickAll(bool picked)
        {
            foreach (var test in _visible)
            {
                if (picked) _picked.Add(test.Name);
                else _picked.Remove(test.Name);
            }

            _list?.RefreshVisible();
            BuildHeader();
        }

        private TestRecord FindRecord(string name) =>
            _report?.Tests.Find(record => record.Name == name);

        private static KUITone ToneOf(TestStatus? status)
        {
            switch (status)
            {
                case TestStatus.Passed: return KUITone.Success;
                case TestStatus.Failed:
                case TestStatus.Error:
                case TestStatus.Timeout: return KUITone.Error;
                case TestStatus.Skipped: return KUITone.Warning;
                default: return KUITone.Neutral;
            }
        }

        // ================================================================ actions

        private void StartRun(List<string> names)
        {
            SaveOptions();

            var options = _options.Clone();
            options.Paths.Clear();

            if (names != null && names.Count > 0)
            {
                foreach (var name in names)
                {
                    var test = _tests.Find(candidate => candidate.Name == name);
                    if (test != null && !string.IsNullOrEmpty(test.SourcePath))
                        options.Paths.Add(test.SourcePath);
                }

                if (options.Paths.Count == 0)
                {
                    KUIToast.Error(rootVisualElement, "The selected tests have no source files to run.");
                    return;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(_search)) options.NameFilter = _search;
                if (!string.IsNullOrEmpty(_tagFilter)) options.Tags.Add(_tagFilter);
            }

            if (EditorTestRunner.Run(options, true, out var problem))
            {
                _wasRunning = true;
                BuildHeader();
                SetStatus("Entering play mode…", KUITone.Accent);
            }
            else
            {
                KUIToast.Error(rootVisualElement, problem);
                SetStatus(problem, KUITone.Error);
            }
        }

        private void RunFailures()
        {
            if (_report == null) return;

            var names = new List<string>();
            foreach (var record in _report.Tests)
                if (record.IsFailure) names.Add(record.Name);

            if (names.Count == 0)
            {
                KUIToast.Success(rootVisualElement, "Nothing failed in the last run.");
                return;
            }

            StartRun(names);
        }

        private void OnRunCompleted(RunReport report)
        {
            _report = report;
            _wasRunning = false;

            if (_shell == null) return;

            BuildHeader();
            BuildSidebar();
            ShowPage(report != null && !report.Success ? Page.Results : _page);
            UpdateStatus();

            if (report == null)
            {
                KUIToast.Error(rootVisualElement, "The run finished but no report was found.");
                return;
            }

            if (report.Success) KUIToast.Success(rootVisualElement, report.Summary());
            else KUIToast.Error(rootVisualElement, report.Summary());
        }

        private void OnRunFailed(string reason)
        {
            _wasRunning = false;

            if (_shell == null) return;

            BuildHeader();
            SetStatus(reason, KUITone.Error);
            KUIToast.Error(rootVisualElement, reason);
        }

        private void OpenHtmlReport()
        {
            var folder = EditorTestRunner.LastRunFolder();
            var html = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "report.html");

            if (!string.IsNullOrEmpty(html) && File.Exists(html))
                Application.OpenURL("file:///" + html.Replace('\\', '/'));
            else
                KUIToast.Error(rootVisualElement, "No HTML report yet — run something first.");
        }

        private static void OpenScript(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                AssetDatabase.OpenAsset(asset);
                return;
            }

            if (File.Exists(path))
                EditorUtility.RevealInFinder(path);
        }

        /// <summary>Opens Package Manager on this package, where the samples are imported from.</summary>
        private static void OpenPackageManager() =>
            UnityEditor.PackageManager.UI.Window.Open("com.kobapps.gametestkit");

        // ================================================================ prefs

        private void LoadOptions()
        {
            _options = GameTesterSettings.Instance.CreateRunOptions();

            var stored = EditorPrefs.GetString(OptionsPrefKey, "");
            if (string.IsNullOrEmpty(stored)) return;

            try { _options = TestScriptParser.ParseOptions(JsonValue.Parse(stored), _options); }
            catch (Exception) { /* stale prefs: fall back to the settings defaults */ }
        }

        private void SaveOptions()
        {
            try
            {
                EditorPrefs.SetString(OptionsPrefKey, TestScriptParser.WriteOptions(_options).ToJson(false));
                EditorPrefs.SetString(SelectionPrefKey, string.Join("\n", _picked));
            }
            catch (Exception) { /* not worth interrupting the user over */ }
        }
    }
}
