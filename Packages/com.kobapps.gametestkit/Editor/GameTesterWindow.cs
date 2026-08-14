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
        private const string CollapsedPrefKey = "GameTestKit.CollapsedCategories";
        private const string GroupPrefKey = "GameTestKit.GroupByCategory";

        private enum Page
        {
            Tests = 0,
            Results = 1,
            Options = 2,
        }

        /// <summary>
        /// One line in the test list: a category header, or a test sitting under one.
        /// </summary>
        /// <remarks>
        /// The list is virtualised, so a tree has to be flattened into rows before it can be shown. A
        /// header carries the recursive test count, because "Shop (12)" is the number a person wants
        /// even when eleven of those tests are really in <c>Shop/Checkout</c>.
        /// </remarks>
        private sealed class TestRow
        {
            public GameTest Test;      // null on a header
            public string Category;    // the header's category, or the test's
            public int Depth;
            public int Count;          // header only: tests underneath, nested categories included

            /// <summary>
            /// The last run's verdict for this row — one test's, or the whole group's. Resolved when
            /// the rows are built rather than when they are drawn: binding runs on every scroll, and
            /// searching the report from there turned a scroll into a scan of every test in the suite.
            /// </summary>
            public TestStatus? Status;

            public string StatusTip;

            public bool IsHeader => Test == null;
        }

        /// <summary>Counts for one category, so a header can show a verdict without re-reading the report.</summary>
        private sealed class GroupTally
        {
            public int Passed;
            public int Failed;
            public int Skipped;
            public int NotRun;

            public int Total => Passed + Failed + Skipped + NotRun;

            /// <summary>
            /// One failure anywhere underneath makes the group red: the point of a folded group is that
            /// you can trust it without unfolding it.
            /// </summary>
            public TestStatus? Status =>
                Failed > 0 ? TestStatus.Failed
                : Passed > 0 ? TestStatus.Passed
                : Skipped > 0 ? (TestStatus?)TestStatus.Skipped
                : null;

            public string Describe()
            {
                var parts = new List<string>();
                if (Passed > 0) parts.Add($"{Passed} passed");
                if (Failed > 0) parts.Add($"{Failed} failed");
                if (Skipped > 0) parts.Add($"{Skipped} skipped");
                if (NotRun > 0) parts.Add($"{NotRun} not run");

                return parts.Count == 0 ? "Empty" : string.Join(", ", parts);
            }
        }

        // --- data ---------------------------------------------------------------------------------

        private readonly List<GameTest> _tests = new List<GameTest>();
        private readonly List<GameTest> _visible = new List<GameTest>();
        private readonly List<TestRow> _rows = new List<TestRow>();
        private readonly List<string> _loadErrors = new List<string>();
        private readonly HashSet<string> _picked = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last report indexed for lookup, so a row never searches it linearly.</summary>
        private readonly Dictionary<string, TestRecord> _recordsByPath =
            new Dictionary<string, TestRecord>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, TestRecord> _recordsByIdentity =
            new Dictionary<string, TestRecord>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, GroupTally> _tallies =
            new Dictionary<string, GroupTally>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One test's verdict as it arrived mid-run, before the report exists.</summary>
        private sealed class LiveResult
        {
            public TestStatus Status;
            public string Tip;
        }

        /// <summary>
        /// Verdicts streamed by the run in flight, keyed by script path and by category+name.
        /// </summary>
        /// <remarks>
        /// A run happens in play mode and the report is only written at the end, so the window used to
        /// sit blank until the whole batch finished — the longer the suite, the less it told you. These
        /// come from the same <see cref="LiveStatus"/> heartbeat the Live Run window and the agent API
        /// already poll, so nobody has to agree with anybody about what the run is doing.
        /// </remarks>
        private readonly Dictionary<string, LiveResult> _liveResults =
            new Dictionary<string, LiveResult>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while rows show this run's results rather than the last report's.</summary>
        private bool _streaming;

        /// <summary>
        /// The tests this run covers, or null when it covers everything.
        /// </summary>
        /// <remarks>
        /// Without it, running one test blanks the marks on all the others: they have no result in this
        /// batch, so they read as "not run". A test that is not part of the run has not changed, and
        /// keeps whatever the last report said about it.
        /// </remarks>
        private HashSet<string> _runScope;

        private string _liveHeartbeat;
        private string _liveSummary;

        private RunOptions _options;
        private RunReport _report;
        private string _search = "";
        private string _tagFilter = "";
        private string _categoryFilter = "";
        private bool _groupByCategory = true;
        private Page _page;
        private int _detailIndex = -1;
        private bool _wasRunning;

        // --- ui -----------------------------------------------------------------------------------

        private KUIWindowShell _shell;
        private KUIVirtualList<TestRow> _list;
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
            LoadCategoryState();
            Refresh();

            _report = EditorTestRunner.LoadLastReport();
            IndexReport();

            _page = (Page)Mathf.Clamp(EditorPrefs.GetInt(PagePrefKey, 0), 0, 2);

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
                    string.IsNullOrEmpty(_categoryFilter) ? "All categories" : _categoryFilter,
                    KUITheme.Accent,
                    anchor => BuildCategoryMenu().ShowUnder(anchor),
                    "Only run and show tests in this category — nested categories included."));

                controls.Add(new KUIPill(
                    string.IsNullOrEmpty(_tagFilter) ? "All tags" : _tagFilter,
                    KUITheme.Accent,
                    anchor => BuildTagMenu().ShowUnder(anchor),
                    "Only run and show tests carrying this tag."));

                controls.Add(KUILayout.VerticalSeparator());

                var refresh = KUIButton.Secondary("Refresh", Refresh);
                refresh.tooltip = "Re-scan the project for .gametest.json scripts.";
                controls.Add(refresh);

                var create = KUIButton.Secondary("New Test", () => CreateTestIn(SelectedCategory()));
                create.tooltip = "Create a template script in the selected category.";
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

        // --- categories ---------------------------------------------------------------------------

        /// <summary>Every category in the project, whether or not the current filters show its tests.</summary>
        private List<string> AllCategories() => GameTestCatalog.DiscoverCategories(_tests);

        private KUIMenu BuildCategoryMenu()
        {
            var categories = AllCategories();

            var menu = KUIMenu.New()
                .Item("All categories", () => SetCategoryFilter(""), string.IsNullOrEmpty(_categoryFilter));

            if (categories.Count == 0)
                return menu.Separator().Disabled("No categories yet — make a folder under your tests folder");

            menu.Separator();

            foreach (var category in categories)
            {
                var captured = category;
                menu.Item(captured, () => SetCategoryFilter(captured),
                    string.Equals(captured, _categoryFilter, StringComparison.OrdinalIgnoreCase));
            }

            return menu;
        }

        private void SetCategoryFilter(string category)
        {
            _categoryFilter = TestCategory.Normalize(category);
            BuildHeader();
            RefreshList();
        }

        /// <summary>
        /// The category a new test should land in: whatever the list is pointing at, falling back to the
        /// filter. Creating a test while looking at <c>Shop</c> should put it in <c>Shop</c>.
        /// </summary>
        private string SelectedCategory()
        {
            if (_detailIndex >= 0 && _detailIndex < _rows.Count)
            {
                var row = _rows[_detailIndex];
                return row.IsHeader ? row.Category : row.Test.Category;
            }

            return _categoryFilter;
        }

        private void CreateTestIn(string category)
        {
            var path = GameTesterMenu.CreateTestScript(category);
            Refresh();
            SelectTestBySourcePath(path);
        }

        /// <summary>Asks for a name and makes the folder, so an empty category can be prepared up front.</summary>
        private void CreateCategory(string parent)
        {
            var name = CategoryPrompt.Show(
                "New category",
                string.IsNullOrEmpty(parent)
                    ? "Name of the category. It becomes a folder in your tests folder."
                    : $"Name of the category to create inside '{parent}'.",
                "");

            if (string.IsNullOrWhiteSpace(name)) return;

            var category = string.IsNullOrEmpty(parent)
                ? TestCategory.Normalize(name)
                : TestCategory.Normalize($"{parent}/{name}");

            GameTesterMenu.CreateCategory(category);
            _collapsed.Remove(category);
            SaveCategoryState();
            Refresh();
        }

        private void MoveTest(GameTest test, string category)
        {
            if (test == null || string.IsNullOrEmpty(test.SourcePath)) return;

            var moved = GameTesterMenu.MoveTestToCategory(test.SourcePath, category);
            if (string.IsNullOrEmpty(moved))
            {
                KUIToast.Error(rootVisualElement, "Unity would not move that script — see the Console.");
                return;
            }

            _collapsed.Remove(TestCategory.Normalize(category));
            SaveCategoryState();
            Refresh();
            SelectTestBySourcePath(moved);
            KUIToast.Success(rootVisualElement, $"Moved to {TestCategory.Display(category)}");
        }

        /// <summary>A menu of every category plus "new one", used by both the row menu and the detail pane.</summary>
        private KUIMenu BuildMoveMenu(GameTest test) => AppendMoveItems(KUIMenu.New(), test, "");

        /// <summary>
        /// Adds one "move this test there" entry per category. A category's own <c>/</c> separators
        /// become submenus, so the menu is shaped like the tree it is choosing from.
        /// </summary>
        private KUIMenu AppendMoveItems(KUIMenu menu, GameTest test, string prefix)
        {
            bool movable = !string.IsNullOrEmpty(test.SourcePath);

            menu.Item(prefix + TestCategory.UncategorizedLabel, () => MoveTest(test, ""),
                movable, string.IsNullOrEmpty(test.Category));

            var categories = AllCategories();
            if (categories.Count > 0) menu.Separator(prefix.TrimEnd('/'));

            foreach (var category in categories)
            {
                var captured = category;
                menu.Item(prefix + captured, () => MoveTest(test, captured),
                    movable, string.Equals(captured, test.Category, StringComparison.OrdinalIgnoreCase));
            }

            menu.Separator(prefix.TrimEnd('/'));
            menu.Item(prefix + "New category…", () =>
            {
                var name = CategoryPrompt.Show("Move to a new category",
                    "Name of the category to create and move this test into.", test.Category);

                if (!string.IsNullOrWhiteSpace(name)) MoveTest(test, name);
            }, movable, false);

            return menu;
        }

        /// <summary>Reveals a script in the list — used after creating or moving one.</summary>
        private void SelectTestBySourcePath(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || _list == null) return;

            var index = IndexOfTest(sourcePath.Replace('\\', '/'));
            if (index < 0) return;

            _detailIndex = index;
            _list.SelectedIndex = index;
            _list.ScrollToIndex(index);
            ShowDetail();
        }

        private void ToggleCollapsed(string category)
        {
            if (!_collapsed.Remove(category)) _collapsed.Add(category);
            SaveCategoryState();
            RefreshList();
        }

        private void SetAllCollapsed(bool collapsed)
        {
            _collapsed.Clear();
            if (collapsed)
                foreach (var category in AllCategories()) _collapsed.Add(category);

            SaveCategoryState();
            RefreshList();
        }

        private void LoadCategoryState()
        {
            _groupByCategory = EditorPrefs.GetBool(GroupPrefKey, true);

            _collapsed.Clear();
            foreach (var category in EditorPrefs.GetString(CollapsedPrefKey, "").Split('\n'))
                if (!string.IsNullOrEmpty(category)) _collapsed.Add(category);
        }

        private void SaveCategoryState()
        {
            EditorPrefs.SetBool(GroupPrefKey, _groupByCategory);
            EditorPrefs.SetString(CollapsedPrefKey, string.Join("\n", _collapsed));
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
            sidebar.Add("Options", () => ShowPage(Page.Options));

            sidebar.AddSeparator();

            var categories = AllCategories().Count;
            if (categories > 0)
                sidebar.AddFootnote($"{_tests.Count} tests in {categories} categor{(categories == 1 ? "y" : "ies")}.");

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
                SetStatus(_liveSummary ?? "Running in play mode…", KUITone.Accent);
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

            if (running != _wasRunning)
            {
                _wasRunning = running;
                BuildHeader();
                UpdateStatus();

                // The per-row run buttons are disabled during a run, so they have to hear about it too.
                _list?.RefreshVisible();
            }

            // Every tick while a run is in flight, not only when it starts and stops: this is what
            // fills the list in test by test instead of all at once at the end.
            if (running) PollLiveResults();
        }

        /// <summary>
        /// Reads the run's heartbeat and fills in whatever has finished since the last tick.
        /// </summary>
        /// <remarks>
        /// Cheap by design: the file is only re-read when its <c>heartbeatUtc</c> changes, and applying
        /// results updates the existing rows in place rather than rebuilding the list — so the scroll
        /// position and the selected test survive a run happening underneath them.
        /// </remarks>
        private void PollLiveResults()
        {
            string text;
            try
            {
                if (!File.Exists(LiveStatus.Path)) return;
                text = File.ReadAllText(LiveStatus.Path);
            }
            catch (IOException)
            {
                // Being written this instant. The next tick gets it.
                return;
            }

            JsonValue json;
            try { json = JsonValue.Parse(text); }
            catch (Exception) { return; }

            var beat = json["heartbeatUtc"].AsString("");
            if (string.IsNullOrEmpty(beat) || beat == _liveHeartbeat) return;
            _liveHeartbeat = beat;

            _liveResults.Clear();

            foreach (var item in json["completed"])
            {
                var status = ParseStatus(item["status"].AsString(""));
                if (status == null) continue;

                var message = item["message"].AsString("");
                var result = new LiveResult
                {
                    Status = status.Value,
                    Tip = $"{status.Value} · {item["seconds"].AsNumber():0.00}s" +
                          (string.IsNullOrEmpty(message) ? "" : $"\n{message}"),
                };

                var source = item["source"].AsString("");
                if (!string.IsNullOrEmpty(source)) _liveResults[source.Replace('\\', '/')] = result;

                _liveResults[Identity(item["category"].AsString(""), item["name"].AsString(""))] = result;
            }

            var done = json["testIndex"].AsInt();
            var total = json["testCount"].AsInt();
            var passed = json["passed"].AsInt();
            var failed = json["failed"].AsInt();
            var current = json["test"].AsString("");

            _liveSummary = $"Running {done}/{total} — {passed} passed, {failed} failed" +
                           (string.IsNullOrEmpty(current) ? "" : $"   ·   {current}");

            _streaming = true;
            RefreshStatuses();
            UpdateStatus();
        }

        private static TestStatus? ParseStatus(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return Enum.TryParse<TestStatus>(value, true, out var status) ? status : (TestStatus?)null;
        }

        /// <summary>
        /// The verdict a row shows: this run's, while one is in flight; the last report's otherwise.
        /// </summary>
        /// <remarks>
        /// While streaming, a test with no result yet reads as "not run" rather than keeping the verdict
        /// from the previous batch — a stale green beside a test that is about to fail is worse than no
        /// mark at all.
        /// </remarks>
        private TestStatus? Verdict(GameTest test, out string tip)
        {
            if (_streaming && InRunScope(test))
            {
                if (TryLive(test, out var live))
                {
                    tip = live.Tip;
                    return live.Status;
                }

                tip = "Not run yet in this batch";
                return null;
            }

            var record = FindRecord(test);

            tip = record == null
                ? "Not run in the last batch"
                : $"{record.Status} · {record.DurationSeconds:0.00}s" +
                  (string.IsNullOrEmpty(record.Message) ? "" : $"\n{record.Message}");

            return record?.Status;
        }

        /// <summary>True when this run is going to produce a verdict for that test.</summary>
        private bool InRunScope(GameTest test) =>
            _runScope == null || _runScope.Contains(KeyOf(test));

        /// <summary>
        /// Blanks the marks for the tests a starting run covers, and leaves every other test showing
        /// what the last report said about it.
        /// </summary>
        /// <param name="keys">The tests being run, or null for "everything that passes the filters".</param>
        private void BeginStreaming(IEnumerable<string> keys)
        {
            _runScope = keys == null ? null : new HashSet<string>(keys, StringComparer.Ordinal);
            if (_runScope != null && _runScope.Count == 0) _runScope = null;

            _streaming = true;
            _liveResults.Clear();
            _liveHeartbeat = null;
            _liveSummary = "Entering play mode…";

            RefreshStatuses();
        }

        private void StopStreaming()
        {
            _streaming = false;
            _runScope = null;
            _liveResults.Clear();
            _liveHeartbeat = null;
            _liveSummary = null;
        }

        private bool TryLive(GameTest test, out LiveResult result)
        {
            if (!string.IsNullOrEmpty(test.SourcePath) &&
                _liveResults.TryGetValue(test.SourcePath.Replace('\\', '/'), out result))
                return true;

            return _liveResults.TryGetValue(Identity(test.Category, test.Name), out result);
        }

        /// <summary>Re-reads every row's verdict without rebuilding the list.</summary>
        private void RefreshStatuses()
        {
            RebuildTallies();

            foreach (var row in _rows)
            {
                if (row.IsHeader)
                {
                    var tally = TallyFor(row.Category);
                    row.Count = tally.Total;
                    row.Status = tally.Status;
                    row.StatusTip = tally.Describe();
                }
                else
                {
                    row.Status = Verdict(row.Test, out var tip);
                    row.StatusTip = tip;
                }
            }

            _list?.RefreshVisible();
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
                    + "a flow by playing it once and letting the recorder write the script for you. The "
                    + "folder a script sits in becomes its category, so start folders early.",
                    "New Test",
                    () => CreateTestIn(""),
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

            var group = KUIButton.Ghost(_groupByCategory ? "☰ Grouped" : "☰ Flat", () =>
            {
                _groupByCategory = !_groupByCategory;
                SaveCategoryState();
                ShowPage(Page.Tests);
            });
            group.tooltip = _groupByCategory
                ? "Showing tests grouped by category. Click for one flat list."
                : "Showing one flat list. Click to group by category.";
            toolbar.With(group);

            var categoryMenu = KUIButton.Ghost("Categories ▾", () =>
                KUIMenu.New()
                    .Item("New category…", () => CreateCategory(""))
                    .Item($"New category in '{TestCategory.Display(SelectedCategory())}'…",
                        () => CreateCategory(SelectedCategory()),
                        !string.IsNullOrEmpty(SelectedCategory()), false)
                    .Separator()
                    .Item("Expand all", () => SetAllCollapsed(false))
                    .Item("Collapse all", () => SetAllCollapsed(true))
                    .Separator()
                    .Item("Reveal tests folder", RevealTestsFolder)
                    .ShowAtCursor());
            categoryMenu.tooltip = "A category is a folder under your tests folder.";
            toolbar.With(categoryMenu);

            toolbar.With(KUIButton.Ghost("All", () => PickAll(true)));
            toolbar.With(KUIButton.Ghost("None", () => PickAll(false)));
            left.Add(toolbar);

            RebuildVisible();

            _list = new KUIVirtualList<TestRow>(_rows, MakeRow, BindRow, 26f, (row, _) => row.IsHeader ? 24f : 26f)
            {
                EmptyMessage = "No test matches the current filter.",
            };
            _list.SelectionChanged += index =>
            {
                if (index < 0 || index >= _rows.Count) return;

                // Clicking a header folds it rather than selecting it — there is nothing to show in the
                // detail pane for a folder, and folding is what the row is for.
                if (_rows[index].IsHeader)
                {
                    ToggleCollapsed(_rows[index].Category);
                    return;
                }

                // Moving off a script with unsaved edits asks first, and stays put if the answer is no.
                if (index != _detailIndex && !ConfirmDiscard())
                {
                    _list.SelectedIndex = _detailIndex;
                    return;
                }

                _detailIndex = index;
                ShowDetail();
            };
            left.Add(_list);

            split.First.Add(left);

            // --- detail ---------------------------------------------------
            // A column, not a scrolling page: the Script tab holds a code editor, which has to own its
            // own height. Each tab supplies its own scrolling if it wants any.
            _detailPane = KUILayout.Column();
            _detailPane.style.flexGrow = 1;
            _detailPane.style.minHeight = 0;
            split.Second.Add(_detailPane);

            root.Add(split);

            if (_detailIndex >= 0 && _detailIndex < _rows.Count && !_rows[_detailIndex].IsHeader)
                _list.SelectedIndex = _detailIndex;
            else
                _detailIndex = -1;

            ShowDetail();
            return root;
        }

        private static void RevealTestsFolder()
        {
            var folder = GameTesterMenu.CategoryFolder("");
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folder);

            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
            else if (Directory.Exists(folder))
            {
                EditorUtility.RevealInFinder(folder);
            }
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList(KUIClass.ListItem);

            // The triangle is a label rather than a Foldout: a Foldout brings its own row layout and
            // toggle, and this row already has both.
            var twisty = new Label { name = "twisty" };
            twisty.AddToClassList(KUIClass.Muted);
            twisty.style.width = 12;
            twisty.style.flexShrink = 0;
            row.Add(twisty);

            var pick = new Toggle { name = "pick" };
            pick.style.marginLeft = 2;
            pick.style.marginRight = 4;
            pick.RegisterValueChangedCallback(evt =>
            {
                if (!(row.userData is int index) || index < 0 || index >= _rows.Count) return;
                Pick(_rows[index], evt.newValue);
            });

            // Without this the tick also reaches the row, which would fold the very group being ticked.
            pick.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            row.Add(pick);

            // A glyph, not a coloured dot: ✓ and ✗ are what the Test Runner trained everyone to read,
            // and they survive being printed, screenshotted or looked at by someone colour-blind.
            var status = new Label { name = "status" };
            status.style.width = 16;
            status.style.flexShrink = 0;
            status.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(status);

            var label = new Label { name = "label" };
            label.AddToClassList(KUIClass.ListItemLabel);
            label.style.flexGrow = 1;
            row.Add(label);

            var trailing = new Label { name = "tags" };
            trailing.AddToClassList(KUIClass.Muted);
            row.Add(trailing);

            var run = new Button { name = "run", text = "▶" };
            run.AddToClassList(KUIClass.Button);
            run.AddToClassList(KUIClass.ButtonGhost);
            run.style.flexShrink = 0;
            run.style.paddingLeft = 4;
            run.style.paddingRight = 4;
            run.style.marginLeft = 2;
            run.clicked += () =>
            {
                if (!(row.userData is int index) || index < 0 || index >= _rows.Count) return;

                var item = _rows[index];
                if (item.IsHeader) RunCategory(item.Category);
                else StartRun(new List<string> { KeyOf(item.Test) });
            };

            // The button owns its click; letting it through would fold the group as well as run it.
            run.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            row.Add(run);

            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 1) return;
                if (!(row.userData is int index) || index < 0 || index >= _rows.Count) return;

                ShowRowMenu(_rows[index]);
                evt.StopPropagation();
            });

            return row;
        }

        private void BindRow(VisualElement row, TestRow item, int index)
        {
            row.userData = index;

            var twisty = row.Q<Label>("twisty");
            var pick = row.Q<Toggle>("pick");
            var status = row.Q<Label>("status");
            var label = row.Q<Label>("label");
            var trailing = row.Q<Label>("tags");
            var run = row.Q<Button>("run");

            // Depth is drawn as padding on the row so the whole strip, including its hover highlight,
            // steps in with the tree.
            row.style.paddingLeft = 4 + item.Depth * 14;
            row.EnableInClassList(KUIClass.ListItemOdd, false);

            if (item.IsHeader)
            {
                bool collapsed = _collapsed.Contains(item.Category);

                if (twisty != null)
                {
                    twisty.style.display = DisplayStyle.Flex;
                    twisty.text = collapsed ? "▸" : "▾";
                }

                ApplyStatus(status, item.Status, item.StatusTip);

                if (pick != null)
                {
                    pick.style.display = DisplayStyle.Flex;
                    pick.SetValueWithoutNotify(IsWholeGroupPicked(item.Category));
                    pick.tooltip = "Select every test in this category.";
                }

                if (run != null)
                {
                    run.style.display = DisplayStyle.Flex;
                    run.tooltip = $"Run the {item.Count} test(s) in {TestCategory.Display(item.Category)}.";
                    run.SetEnabled(!EditorTestRunner.IsRunning && item.Count > 0);
                }

                if (label != null)
                {
                    // Only the last segment: the rows above it already show the path.
                    var leaf = TestCategory.Leaf(item.Category);
                    label.text = leaf.Length > 0 ? leaf : TestCategory.Display(item.Category);
                    label.tooltip = TestCategory.Display(item.Category);
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                    label.style.opacity = 1f;
                }

                if (trailing != null) trailing.text = item.Count.ToString();
                return;
            }

            var test = item.Test;

            if (twisty != null) twisty.style.display = DisplayStyle.None;

            if (pick != null)
            {
                pick.style.display = DisplayStyle.Flex;
                pick.SetValueWithoutNotify(_picked.Contains(KeyOf(test)));
                pick.tooltip = null;
            }

            ApplyStatus(status, item.Status, item.StatusTip);

            if (run != null)
            {
                run.style.display = DisplayStyle.Flex;
                run.tooltip = $"Run '{test.Name}'.";
                run.SetEnabled(!EditorTestRunner.IsRunning && !string.IsNullOrEmpty(test.SourcePath));
            }

            if (label != null)
            {
                label.text = test.Name;
                label.tooltip = test.SourcePath;
                label.style.unityFontStyleAndWeight = FontStyle.Normal;
            }

            if (trailing != null)
            {
                // Ungrouped, the category is the useful trailing note; grouped, the header already
                // says it and the tags are what is left worth showing.
                trailing.text = !_groupByCategory && !string.IsNullOrEmpty(test.Category)
                    ? test.Category
                    : test.Tags.Count > 0 ? string.Join(" ", test.Tags) : "";
            }
        }

        // --- status glyphs ------------------------------------------------------------------------

        /// <summary>
        /// Paints a row's status the way the Test Runner does: ✓, ✗, or an empty circle for a test
        /// that has not run. Null means "no result".
        /// </summary>
        private static void ApplyStatus(Label glyph, TestStatus? status, string tooltip)
        {
            if (glyph == null) return;

            glyph.style.display = DisplayStyle.Flex;
            glyph.tooltip = tooltip;

            switch (status)
            {
                case TestStatus.Passed:
                    glyph.text = "✓";
                    glyph.style.color = KUITheme.Success;
                    break;

                case TestStatus.Failed:
                case TestStatus.Error:
                case TestStatus.Timeout:
                    glyph.text = "✗";
                    glyph.style.color = KUITheme.Error;
                    break;

                case TestStatus.Skipped:
                    glyph.text = "–";
                    glyph.style.color = KUITheme.Warning;
                    break;

                default:
                    glyph.text = "○";
                    glyph.style.color = KUITheme.Muted;
                    break;
            }
        }

        /// <summary>
        /// Counts every visible test into its own category and each category above it, in one pass. A
        /// test in <c>Shop/Checkout</c> therefore counts towards both <c>Shop/Checkout</c> and
        /// <c>Shop</c>, which is what makes a folded parent's verdict trustworthy.
        /// </summary>
        private void RebuildTallies()
        {
            _tallies.Clear();

            foreach (var test in _visible)
            {
                var status = Verdict(test, out _);

                if (string.IsNullOrEmpty(test.Category))
                {
                    // The Uncategorized group is a leaf: it holds only what has no category at all.
                    Count("", status);
                    continue;
                }

                foreach (var category in TestCategory.SelfAndAncestors(test.Category))
                    Count(category, status);
            }

            void Count(string category, TestStatus? status)
            {
                if (!_tallies.TryGetValue(category, out var tally))
                    _tallies[category] = tally = new GroupTally();

                if (status == null) tally.NotRun++;
                else if (status == TestStatus.Failed || status == TestStatus.Error ||
                         status == TestStatus.Timeout) tally.Failed++;
                else if (status == TestStatus.Skipped) tally.Skipped++;
                else if (status == TestStatus.Passed) tally.Passed++;
                else tally.NotRun++;
            }
        }

        private GroupTally TallyFor(string category) =>
            _tallies.TryGetValue(TestCategory.Normalize(category), out var tally) ? tally : new GroupTally();

        /// <summary>Ticking a header ticks everything under it; ticking a test ticks just that one.</summary>
        private void Pick(TestRow item, bool picked)
        {
            if (item.IsHeader)
            {
                foreach (var test in _visible)
                {
                    if (!InGroup(test, item.Category)) continue;

                    if (picked) _picked.Add(KeyOf(test));
                    else _picked.Remove(KeyOf(test));
                }

                _list?.RefreshVisible();
            }
            else
            {
                if (picked) _picked.Add(KeyOf(item.Test));
                else _picked.Remove(KeyOf(item.Test));
            }

            BuildHeader();
        }

        private bool IsWholeGroupPicked(string category)
        {
            bool any = false;

            foreach (var test in _visible)
            {
                if (!InGroup(test, category)) continue;
                if (!_picked.Contains(KeyOf(test))) return false;
                any = true;
            }

            return any;
        }

        /// <summary>The right-click menu: where a test is filed, and what to do with the category.</summary>
        private void ShowRowMenu(TestRow item)
        {
            if (item.IsHeader)
            {
                KUIMenu.New()
                    .Item($"Run '{TestCategory.Display(item.Category)}'", () => RunCategory(item.Category))
                    .Item("Select these tests", () => Pick(item, true))
                    .Separator()
                    .Item("New test here…", () => CreateTestIn(item.Category))
                    .Item("New category inside…", () => CreateCategory(item.Category))
                    .Separator()
                    .Item("Filter to this category", () => SetCategoryFilter(item.Category))
                    .Item("Reveal folder", () => RevealCategory(item.Category))
                    .ShowAtCursor();
                return;
            }

            var test = item.Test;
            bool hasScript = !string.IsNullOrEmpty(test.SourcePath);

            var menu = KUIMenu.New()
                .Item("Run", () => StartRun(new List<string> { KeyOf(test) }))
                .Separator()
                .Item("Select script in Project", () => SelectScript(test.SourcePath), hasScript, false)
                .Item("Open script", () => OpenScript(test.SourcePath), hasScript, false)
                .Separator();

            AppendMoveItems(menu, test, "Move to/");

            menu.Separator()
                .Item("Delete script…", () => DeleteScript(test), hasScript, false)
                .ShowAtCursor();
        }

        private static void RevealCategory(string category)
        {
            var folder = GameTesterMenu.CategoryFolder(category);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folder);

            if (asset == null) return;

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>Runs everything in a category, nested categories included.</summary>
        private void RunCategory(string category)
        {
            var keys = new List<string>();

            foreach (var test in _visible)
                if (InGroup(test, category)) keys.Add(KeyOf(test));

            if (keys.Count == 0)
            {
                KUIToast.Error(rootVisualElement, $"No tests in {TestCategory.Display(category)}.");
                return;
            }

            StartRun(keys);
        }

        /// <summary>
        /// The inspector for the selected test: what it does, and the script itself.
        /// </summary>
        /// <remarks>
        /// The script editor used to be its own page with its own file dropdown — a second list of the
        /// same tests, kept in sync by hand and unusable once there were more than a screenful. It is a
        /// tab on the selected test instead, so the list you already navigate <em>is</em> the file
        /// picker.
        /// </remarks>
        private void ShowDetail()
        {
            if (_detailPane == null) return;

            _detailPane.Clear();

            if (_detailIndex < 0 || _detailIndex >= _rows.Count || _rows[_detailIndex].IsHeader)
            {
                CloseScriptEditor();
                _detailPane.Add(KUIEmptyState.Line("Select a test to see what it does."));
                return;
            }

            var test = _rows[_detailIndex].Test;

            var body = KUILayout.Column();
            body.style.flexGrow = 1;
            body.style.minHeight = 0;

            var tabs = new KUITabBar("GameTestKit.Detail");
            tabs.AddTab("Overview", () => ShowOverviewTab(body, test));
            tabs.AddTab("Script", () => ShowScriptTab(body, test));

            _detailPane.Add(tabs);
            _detailPane.Add(body);

            tabs.RestoreSelection();
        }

        private void ShowOverviewTab(VisualElement body, GameTest test)
        {
            CloseScriptEditor();

            body.Clear();

            var page = KUILayout.Page();
            page.style.flexGrow = 1;
            body.Add(page);

            var record = FindRecord(test);

            var card = new KUICard(test.Name, test.Description);
            card.Header.Add(KUILayout.Spacer());
            card.Header.Add(KUIButton.Primary("▶ Run", () => StartRun(new List<string> { KeyOf(test) })));

            card.Add(KUIText.KeyValueTable(
                "Steps", test.Steps.Count.ToString(),
                "Setup / teardown", $"{test.Setup.Count} / {test.Teardown.Count}",
                "Scene", string.IsNullOrEmpty(test.Scene) ? "— (uses the open scene)" : test.Scene,
                "Tags", test.Tags.Count > 0 ? string.Join(", ", test.Tags) : "—",
                "Timeout", $"{test.TimeoutSeconds:0.#}s",
                "Retries", test.Retries.ToString()));

            // The category is editable rather than merely reported: this is the one place a person is
            // already looking at a test and deciding where it belongs.
            var categoryRow = KUILayout.Row();
            var categoryLabel = KUIText.Muted("Category");
            categoryLabel.style.width = 110;
            categoryRow.Add(categoryLabel);

            var move = KUIButton.Secondary($"{test.CategoryLabel}  ▾",
                () => BuildMoveMenu(test).ShowUnder(categoryRow.worldBound));
            move.tooltip = string.IsNullOrEmpty(test.SourcePath)
                ? "Only tests that came from a file can be moved."
                : "Move the script into another category's folder.";
            move.SetEnabled(!string.IsNullOrEmpty(test.SourcePath));
            categoryRow.Add(move);
            categoryRow.Add(KUILayout.Spacer());
            card.Add(categoryRow);

            if (!string.IsNullOrEmpty(test.SourcePath))
            {
                card.Add(KUILayout.Row(
                    KUIText.Link(test.SourcePath, () => OpenScript(test.SourcePath)),
                    KUILayout.Spacer()));

                var select = KUIButton.Secondary("Select in Project", () => SelectScript(test.SourcePath));
                select.tooltip = "Highlight the script in the Project window without opening it.";

                var open = KUIButton.Secondary("Open", () => OpenScript(test.SourcePath));
                open.tooltip = "Open the script in your external editor. The Script tab edits it here instead.";

                var delete = KUIButton.Danger("Delete…", () => DeleteScript(test));
                delete.tooltip = "Move the script to the recycle bin.";

                card.Add(KUILayout.Row(select, open, KUILayout.Spacer(), delete));
            }

            page.Add(card);

            // The steps, as written. Reading them here beats opening the file to remember what a test
            // does before running it.
            var steps = new KUISection("Steps", true, "GameTestKit.Detail.Steps");
            AppendStepList(steps, test.Setup, "setup");
            AppendStepList(steps, test.Steps, null);
            AppendStepList(steps, test.Teardown, "teardown");
            page.Add(steps);

            if (record != null)
                page.Add(BuildResultCard(record, expanded: true));
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
            var summary = (string.IsNullOrEmpty(record.Category) ? "" : $"{record.Category} · ")
                          + $"{record.Status} · {record.DurationSeconds:0.00}s"
                          + (record.Attempt > 1 ? $" · attempt {record.Attempt}" : "");

            var card = new KUIExpandableCard(record.Name, summary, expanded);

            var glyph = new Label();
            glyph.style.width = 16;
            glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
            ApplyStatus(glyph, record.Status, record.Status.ToString());
            card.Header.Insert(0, glyph);

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
            _picked.RemoveWhere(key => !_tests.Exists(test => KeyOf(test) == key));

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

                if (!test.IsInCategory(_categoryFilter))
                    continue;

                _visible.Add(test);
            }

            RebuildRows();
        }

        /// <summary>
        /// Flattens the visible tests into list rows: a header per category, then the tests filed
        /// directly under it, with collapsed categories hiding everything beneath them.
        /// </summary>
        private void RebuildRows()
        {
            _rows.Clear();
            RebuildTallies();

            // A suite with no categories at all should look exactly as it did before there were any.
            bool anyCategory = false;
            foreach (var test in _visible)
                if (!string.IsNullOrEmpty(test.Category)) { anyCategory = true; break; }

            if (!_groupByCategory || !anyCategory)
            {
                foreach (var test in _visible) _rows.Add(TestRowFor(test, 0));
                return;
            }

            // Every category with tests, plus the ancestors that hold them together — a test in
            // Shop/Checkout needs a Shop header even when nothing sits in Shop itself.
            var categories = GameTestCatalog.DiscoverCategories(_visible);
            bool anyUncategorized = false;

            foreach (var test in _visible)
                if (string.IsNullOrEmpty(test.Category)) { anyUncategorized = true; break; }

            if (anyUncategorized) AppendGroup("", 0);

            foreach (var category in categories)
            {
                if (IsHiddenByCollapse(category)) continue;

                AppendGroup(category, TestCategory.Segments(category).Length - 1);
            }

            void AppendGroup(string category, int depth)
            {
                var tally = TallyFor(category);

                _rows.Add(new TestRow
                {
                    Category = category,
                    Depth = depth,
                    Count = tally.Total,
                    Status = tally.Status,
                    StatusTip = tally.Describe(),
                });

                if (_collapsed.Contains(category)) return;

                foreach (var test in _visible)
                    if (string.Equals(test.Category, category, StringComparison.OrdinalIgnoreCase))
                        _rows.Add(TestRowFor(test, depth + 1));
            }
        }

        /// <summary>A row for one test, with its last result resolved once rather than on every scroll.</summary>
        private TestRow TestRowFor(GameTest test, int depth)
        {
            var status = Verdict(test, out var tip);

            return new TestRow
            {
                Test = test,
                Category = test.Category,
                Depth = depth,
                Status = status,
                StatusTip = tip,
            };
        }

        /// <summary>True when a category sits inside a collapsed one and must not be drawn at all.</summary>
        private bool IsHiddenByCollapse(string category)
        {
            var parent = TestCategory.Parent(category);

            while (!string.IsNullOrEmpty(parent))
            {
                if (_collapsed.Contains(parent)) return true;
                parent = TestCategory.Parent(parent);
            }

            return false;
        }

        /// <summary>
        /// Whether a test belongs to a list group. Distinct from <see cref="GameTest.IsInCategory"/>,
        /// where an empty category is a filter matching everything: as a <em>group</em>, empty is the
        /// Uncategorized header, and it holds only the tests that really have no category.
        /// </summary>
        private static bool InGroup(GameTest test, string category) =>
            string.IsNullOrEmpty(category)
                ? string.IsNullOrEmpty(test.Category)
                : test.IsInCategory(category);

        private void RefreshList()
        {
            // Folding a category rebuilds every row, and losing the open test each time you tidy the
            // tree makes the tree feel hostile. The selection is re-found by script instead.
            var selected = _detailIndex >= 0 && _detailIndex < _rows.Count && !_rows[_detailIndex].IsHeader
                ? KeyOf(_rows[_detailIndex].Test)
                : null;

            RebuildVisible();

            if (_list == null) return;

            _list.SetItems(_rows);

            _detailIndex = selected == null ? -1 : IndexOfTest(selected);
            if (_detailIndex >= 0) _list.SelectedIndex = _detailIndex;

            // Only when the inspected test actually changed. Filtering the list happens on every
            // keystroke, and rebuilding a code editor that many times is both slow and pointless — the
            // same test is still open in it.
            bool stillShowingTheSameTest = selected != null && _detailIndex >= 0;
            if (!stillShowingTheSameTest) ShowDetail();
        }

        private int IndexOfTest(string key)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (!_rows[i].IsHeader && KeyOf(_rows[i].Test) == key) return i;

            return -1;
        }

        private void PickAll(bool picked)
        {
            foreach (var test in _visible)
            {
                if (picked) _picked.Add(KeyOf(test));
                else _picked.Remove(KeyOf(test));
            }

            _list?.RefreshVisible();
            BuildHeader();
        }

        /// <summary>
        /// The last run's result for a test. Matched on the script rather than the name, so two
        /// same-named tests in different categories do not wear each other's result.
        /// </summary>
        private TestRecord FindRecord(GameTest test)
        {
            if (test == null) return null;

            if (!string.IsNullOrEmpty(test.SourcePath) &&
                _recordsByPath.TryGetValue(test.SourcePath.Replace('\\', '/'), out var byPath))
                return byPath;

            return _recordsByIdentity.TryGetValue(Identity(test.Category, test.Name), out var byIdentity)
                ? byIdentity
                : null;
        }

        /// <summary>
        /// Indexes the report so a row can find its result in one lookup. Called whenever
        /// <see cref="_report"/> changes — binding a row happens on every scroll, and searching a
        /// thousand-test report from there is what makes a list feel broken.
        /// </summary>
        private void IndexReport()
        {
            _recordsByPath.Clear();
            _recordsByIdentity.Clear();

            if (_report == null) return;

            foreach (var record in _report.Tests)
            {
                if (!string.IsNullOrEmpty(record.SourcePath))
                    _recordsByPath[record.SourcePath.Replace('\\', '/')] = record;

                _recordsByIdentity[Identity(record.Category, record.Name)] = record;
            }
        }

        private static string Identity(string category, string name) =>
            $"{TestCategory.Normalize(category)}/{name}";

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

        /// <summary>
        /// Runs the tests identified by <paramref name="keys"/>, or everything matching the filters
        /// when it is null. Keys, not names: two categories may each hold a test called "smoke".
        /// </summary>
        private void StartRun(List<string> keys)
        {
            SaveOptions();

            var options = _options.Clone();
            options.Paths.Clear();

            if (keys != null && keys.Count > 0)
            {
                foreach (var key in keys)
                {
                    var test = _tests.Find(candidate => KeyOf(candidate) == key);
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
                if (!string.IsNullOrEmpty(_categoryFilter)) options.Categories.Add(_categoryFilter);
            }

            if (EditorTestRunner.Run(options, true, out var problem))
            {
                _wasRunning = true;

                // Clear the marks for what is about to run — but only those. Leaving them up makes a
                // run that has barely started look like one that already passed; clearing all of them
                // would throw away results for tests this run never touches.
                BeginStreaming(keys);

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

            var keys = new List<string>();

            foreach (var record in _report.Tests)
            {
                if (!record.IsFailure) continue;

                // Match on the script the record came from, so a failure in Shop/smoke does not drag
                // Onboarding/smoke into the re-run with it.
                var test = _tests.Find(candidate =>
                    !string.IsNullOrEmpty(record.SourcePath)
                        ? SamePath(candidate.SourcePath, record.SourcePath)
                        : candidate.Name == record.Name &&
                          string.Equals(candidate.Category, record.Category, StringComparison.OrdinalIgnoreCase));

                if (test != null) keys.Add(KeyOf(test));
            }

            if (keys.Count == 0)
            {
                KUIToast.Success(rootVisualElement, "Nothing failed in the last run.");
                return;
            }

            StartRun(keys);
        }

        /// <summary>Identifies a test for selection and re-runs: its script, or its name when it has none.</summary>
        private static string KeyOf(GameTest test) =>
            string.IsNullOrEmpty(test.SourcePath) ? test.Name : test.SourcePath.Replace('\\', '/');

        private static bool SamePath(string left, string right) =>
            !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
            string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private void OnRunCompleted(RunReport report)
        {
            _report = report;
            IndexReport();
            _wasRunning = false;

            // The report is richer than the heartbeat — durations, messages, retries — so hand back.
            StopStreaming();

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
            StopStreaming();

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

        /// <summary>
        /// Selects the script in the Project window and pings it, without opening it in an editor.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="OpenScript"/> because they answer different questions. "Where does
        /// this live?" should not launch an external editor and steal focus — which is exactly what
        /// <c>AssetDatabase.OpenAsset</c> does, and why the two are not one command.
        /// </remarks>
        private static void SelectScript(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

            if (asset == null)
            {
                if (File.Exists(path)) EditorUtility.RevealInFinder(path);
                return;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>
        /// Deletes a test's script, after asking.
        /// </summary>
        /// <remarks>
        /// <see cref="AssetDatabase.MoveAssetToTrash"/> rather than <c>DeleteAsset</c>: a test is
        /// somebody's work, and the difference between the two is whether a mis-click is recoverable
        /// from the OS bin. The dialog names the file and its category, because "Delete?" over a list
        /// where the selection may have moved is how the wrong thing gets deleted.
        /// </remarks>
        private void DeleteScript(GameTest test)
        {
            if (test == null || string.IsNullOrEmpty(test.SourcePath)) return;

            var name = Path.GetFileName(test.SourcePath);

            if (!EditorUtility.DisplayDialog(
                    "Delete this test?",
                    $"{test.Name}\n{name}\nin {TestCategory.Display(test.Category)}\n\n" +
                    "The file moves to the recycle bin.",
                    "Delete", "Cancel"))
                return;

            // The editor may be holding this very file, with unsaved text. Drop it first, or saving
            // later would write the deleted script back.
            if (SamePath(_editingPath, test.SourcePath))
            {
                ForgetPending();
                CloseScriptEditor();
                _editingPath = null;
            }

            if (!AssetDatabase.MoveAssetToTrash(test.SourcePath))
            {
                KUIToast.Error(rootVisualElement, $"Unity would not delete {name} — see the Console.");
                return;
            }

            _picked.Remove(KeyOf(test));
            AssetDatabase.Refresh();

            _detailIndex = -1;
            Refresh();

            KUIToast.Success(rootVisualElement, $"Deleted {name}");
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
