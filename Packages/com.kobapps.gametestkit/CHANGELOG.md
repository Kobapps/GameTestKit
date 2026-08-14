# Changelog

All notable changes to GameTestKit are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-08-14

### Added

- **Testing what the game emits.** A new layer for asserting on analytics events — and on anything
  that has the same shape: server calls, IAP receipts, ad callbacks, remote-config fetches, save
  writes. The kit says *event* rather than *analytics* because one set of verbs covers all of them.
  See [Documentation~/events.md](Documentation~/events.md).
  - **`TestEventLog`** — a session-long window of everything the game emitted, with its payload and
    what each sink did with it. The game feeds it with one line from its own event bus. Thread-safe,
    because an ad or attribution SDK delivering on its own thread is exactly the event worth
    recording. Marks are sequence numbers rather than timestamps, so "since this case opened" stays
    exact when two events share a millisecond. Off in release builds unless asked.
  - **Verbs**: `eventCase`, `expectEvent`, `expectNoEvent`, `waitForEvent`, `expectOrder`,
    `expectOnlyExpectedEvents`, `eventInvariants`, `eventProof`.
  - **A property matcher grammar** — `"*"` present, `"!"` absent, `">=1"`, `"~regex"`,
    `["one","of"]`, `"type:number"`, and `"@const:My.Telemetry.RESULT_*"`, which asserts against the
    constants a class declares instead of a copied literal. That last one is what catches a `"win"`
    written where `"Win"` was meant — a break no equality check against a hand-copied string finds.
  - **Reach as well as payload**: `delivered` / `notDelivered` assert what each sink did with the
    event, because "it fired" and "it arrived" are different claims and a missing dashboard row is
    usually the second one failing.
  - **`eventInvariants`** — the session-wide sweep for defects no single payload reveals: gaps in the
    sequence, two `Level_Start`s with no end between them, the same payload sent twice within
    250 ms, a required property missing from one event in fifty. Point `sequenceProperty` at the
    game's own event counter: the log's numbering is contiguous by construction, since it numbers
    what it *receives*, so only a counter stamped at emit time can show that something was dropped
    on the way. That property is excluded from the duplicate comparison automatically — a value that
    differs by construction would otherwise make every payload unique and switch the check off.
  - **Proof records.** `eventProof` writes an `event-proof-*.json` beside the test's artifacts and a
    panel into the HTML report: the payload in full, per-property verdicts marked in place, which
    sinks took it, and the screenshot of the moment it fired. The whole payload, not only the
    asserted properties — a property nobody asserted is where an unnoticed regression lives.
  - Failures name what *did* happen: `no 'Level_End' was recorded within 30.0s. What did fire:
    Level_Start, Popup_Shown, Booster_Used`.
- **The Demo Game sample covers all of it.** Its tests now sit in nested category folders
  (`Gameplay/Shop`, `Gameplay/Cards`, `Gameplay/Onboarding`, `Analytics`), so the suite that asks
  for `Gameplay` picking up all three is visible rather than described, and it ships a
  miniature analytics hub —
  two providers, a consent switch, super properties — that deliberately holds no reference to
  GameTestKit, plus the one bridge file that joins it to `TestEventLog` and is the file worth
  copying. Four new proof cases show the payload/count/reach assertions, the failure path of an
  event, a well-formed event that must not leave the device, and the session-wide sweep.
  `analytics.gamesuite.json` shows suite fixtures.
- **The GameTester window fills in as the run goes.** Verdicts stream test by test instead of
  appearing all at once when the batch ends, so a long suite tells you something after the first
  test rather than after the last. It reads the same `LiveStatus` heartbeat the Live Run window and
  the agent API already poll — now carrying each finished test's verdict, not just running totals —
  and updates rows in place, so scroll position and the selected test survive. Starting a run clears
  the previous marks rather than leaving stale greens beside tests that have not run yet, and the
  status bar counts progress: `Running 3/8 — 2 passed, 1 failed · <current test>`. Only the tests a
  run actually covers are cleared — running one test leaves every other row's ✓ or ✗ alone, because
  a test the run never touches has not changed.
- **A readiness check for uGUI input**, reported once at the top of a run. Missing EventSystem,
  disabled EventSystem, no active input module, or — the expensive one — an
  `InputSystemUIInputModule` added from a script with no actions assigned, which silently ignores
  every pointer event including simulated ones. All of these fail identically without it: the click
  step passes, nothing in the game reacts, and the test dies at whatever it waited for next with
  nothing in the console.
- **Suite-level `beforeEach` / `afterEach`.** Declared in a `.gamesuite.json` or on
  `GameTesterSettings ▸ Fixtures`, so the reset every case needs is written once instead of copied
  into each file. `beforeEach` runs before a test's own `setup` and a failure there is an *error*;
  `afterEach` always runs and its failures are logged, not fatal — as with `teardown`.

### Fixed

- `TestEventLog` and category derivation degrade gracefully when Unity's engine cannot be reached —
  off the main thread, or in a plain test host. Both isolate the engine call in its own frame,
  because a `try` in the same method as a Unity API does not catch the failure the runtime raises
  while preparing that method.

## [1.1.0] - 2026-08-14

### Added

- **Categories.** A test's folder is now its category, so organising a suite is a matter of dragging
  files into folders — no second place to keep in sync. Categories nest (`Shop/Checkout` is inside
  `Shop`), and a script can override its folder with a `"category"` field when it has to live
  somewhere the folders do not describe.
- **The GameTester window groups by category**: collapsible headers with recursive counts, a
  category filter beside the tag filter, a tick per group that selects everything under it, and a
  **Grouped/Flat** switch. Fold state and the grouping choice survive a restart. Right-clicking a
  header runs, selects or filters to that category; right-clicking a test moves it to another one.
  The detail pane's category button does the same, and **Categories ▾** creates a category.
- **Moving a test between categories moves its script**, through `AssetDatabase.MoveAsset`, so the
  asset keeps its GUID.
- **A ▶ on every row** runs that test, or that whole category including everything nested under it.
- **Select, open and delete a test's script from the window** — on the row's right-click menu and as
  buttons in the inspector. Selecting pings the asset in the Project window without launching an
  external editor; deleting names the file and its category first and moves it to the recycle bin
  rather than erasing it, and drops the script editor if it happens to be holding that file.
- **Test Runner-style status glyphs** in the list — ✓ passed, ✗ failed, – skipped, ○ not run —
  replacing the coloured dot, and shown on the Results page too. A category header carries the
  verdict of everything underneath it, so a folded group still tells you whether it is green; its
  tooltip breaks down the counts. Glyphs rather than colour alone also survive a screenshot and a
  colour-blind reader.
- **The script editor is now a tab on the selected test.** Selecting a test gives you **Overview**
  (steps, scene, tags, category, last result) and **Script** (the JSON, edited in place with live
  completions, inline parse errors, Format and ▶ Run this). Unsaved edits survive switching tabs and
  rebuilds of the pane, and moving to another test asks before discarding them.
- **Category filters everywhere a tag filter already worked**: `-gtk-categories` and
  `-gtk-exclude-categories` on the CLI, `-gametest-categories` on a player command line,
  `"categories"` / `"excludeCategories"` in a `.gamesuite.json`, and `RunOptions.Categories`. They
  include everything nested underneath. Categories and tags are ANDed — asking for category `Shop`
  and tag `smoke` runs the smoke tests in the shop.
- **Reports carry the category**: a heading per category in the HTML report, a `category` field and
  per-category totals in the JSON, and `category` in `AICommands.Catalogue` and `GameTesterCLI.List`
  so an agent files a new test beside its siblings.

### Changed

- **The standalone Script Editor page is gone**, replaced by the per-test Script tab above. Its file
  dropdown was a second list of the same tests, kept in sync by hand and unusable past a screenful.
  The window's page index shifted, so it may reopen on a different page once.
- **Row status is resolved when the list is built, not when a row is drawn.** Binding runs on every
  scroll, and looking a result up from there meant scanning the whole report per row; the report is
  indexed once per run and category counts are accumulated in a single pass.
- **The JUnit `classname` now carries the category** — `GameTestKit.Shop.Checkout` rather than
  `GameTestKit` for every test. This is what CI dashboards group and trend by, so a merged report
  from sharded jobs finally reads as a tree. Dashboards keyed on the old flat classname will see the
  history restart.
- **`Copy Tests To Resources` preserves categories.** `Resources.LoadAll` cannot report the folder an
  asset came from, so each copy now gets its category and name written into the JSON, and a file name
  qualified by the category. Two `smoke.gametest.json` files in different folders no longer overwrite
  each other in the flat mirror.
- Discovery no longer scans `Resources` in the Editor — the AssetDatabase pass already covers it, at
  the real paths a category is derived from.

### Fixed

- Two tests with the same file name in different folders are both discovered. Discovery deduplicated
  by file name alone, which silently dropped one of them; it now keys on the whole path, then on
  category and name once the scripts are parsed. A test dropped into StreamingAssets consequently
  overrides the shipped copy of the same test rather than being ignored.
- The window's selection and **Re-run failures** identify a test by its script rather than its name,
  so two same-named tests in different categories no longer run each other.

## [1.0.0] - 2026-08-01

Initial release.

### Added

- **Real input simulation.** `InputSystemBackend` creates virtual mouse, touchscreen, keyboard and
  gamepad devices and queues state events on them, so simulated input enters the pipeline where the
  OS would put it — actions, interactions, `PlayerInput`, the UI module and `Mouse.current` all
  behave as in a human session. `EventSystemBackend` synthesises uGUI pointer events as a portable
  fallback that also works with the legacy Input Manager.
- **`VirtualUser` gestures** — click, double click, hold, hover, drag, swipe, scroll, type, key
  down/up, gamepad buttons and sticks — interpolated across real frames, with an input speed scale.
- **Locator language** with implicit waiting: `id:`, `#name`, hierarchy paths, `text:` (contains and
  exact), `tag:`, `type:`, `pos:`, `screen:`, `world:`, `[index]` and `>>` descendant scoping. Every
  lookup waits for the element to be visible, interactable and un-covered before acting, and failures
  name what blocked it.
- **`TestId` component** for stable, refactor-proof selectors, including runtime registration for
  spawned objects, and **`TestId.AssignId`** for positional elements (board cells, list rows, spawned
  objects) that have no authored id.
- **`.gametest.json` script format** — 30 step verbs across input, flow, scene/state and assertions,
  with `setup`/`steps`/`teardown` phases, tags, per-test timeouts, repeats and retries. Comments and
  trailing commas are tolerated; parse errors report line and column.
- **`.gamesuite.json` suites** carrying filters and run options.
- **Expression language** for `assert` and `waitFor`, over game state and the screen.
- **`GameTestBindings`** — named values and actions (including coroutines) that expose game internals
  to scripts, registered in code or with the `[GameTestBinding]` attribute.
- **`StepRegistry`** — games can register custom step verbs that then work in JSON and appear in the
  generated documentation and AI catalogue. **`[GameTestSteps]`** marks a static registrar so those
  verbs are discovered by reflection, and therefore exist in the Editor window, the validator and the
  AI catalogue rather than only inside play mode. `StepJson` is public, so a game's step factories
  parse parameters with the same error messages the built-in verbs use.
- **`TestBuilder`** fluent C# API for tests that need real logic.
- **`TestRunner`** with per-step and per-test time budgets, retries, repeats, shuffling with a
  reported seed, stop-on-first-failure, fail-on-logged-error with an `expectLog` escape hatch, and
  automatic screenshots on failure.
- **Reports** in JUnit XML, JSON and self-contained HTML, with per-step timings, artifacts, captured
  log errors and frame-time/memory statistics.
- **GameTester window** — browse, filter, run and inspect results, with live run options. Built on
  [EditorCoreKit](https://github.com/Kobapps/EditorCoreKit) and UI Toolkit, so it follows the theme
  and density the editor is set to: a shell with Tests / Results / Options pages, a split view with a
  virtualised test list, expandable result cards and a live status bar.
- **Inspectors** for `TestId` (with the selector to copy) and `GameTesterSettings`, also on
  EditorCoreKit.
- **Recorder** — capture a live play session as a script, choosing the most stable selector for
  everything touched; F8 inserts an assertion, F9 a screenshot.
- **Batch running** from the Editor, from CI (`GameTesterCLI.Run` with CI exit codes), from a built
  player (`-gametests` command line, scripts shipped in Resources or dropped into StreamingAssets),
  and through the Unity Test Framework via the bridge sample.
- **Input overlay.** Simulated input is visible: a yellow ring follows the virtual pointer, a ripple
  marks every tap, drags draw their path, and a caption strip shows the current step plus the text
  being typed, character by character as the game receives it — so a field silently dropping
  characters looks different from a field that got them. IMGUI at a negative `GUI.depth`, so it sits
  above every camera and canvas with no scene setup, and it is part of the presented frame, which
  puts it in failure screenshots too. On by default; toggle with **F9**,
  `Tools ▸ GameTestKit ▸ Input Overlay`, the Options page, `"showInputOverlay": false`,
  `-gtk-no-overlay`, or `InputOverlay.Enabled`. Custom steps can add markers and captions through
  `InputOverlay.Ripple` / `InputOverlay.Note`. Nothing is created in batch mode or `-nographics`.
- **Bots.** `GameBot` is a `ScriptableObject` persona: it senses the game through `BotContext` and
  returns a `BotAction`, which `BotDriver` performs as real pointer input. A bot has no way to call
  into the game, so a flow it cannot complete is a flow a player cannot complete. Personas differ by
  serialized configuration, so a team ships "Casual", "Optimal" and "Whale" as three assets off one
  script.
- **`{"runBot": …}`** with `until` / `failIf` / `seconds` / `actions` and three expectations —
  `goal` (this persona must finish this flow), `clean` (no errors, no dead ends) and `explore`
  (report only, never fails).
- **`RandomTapBot`** — a game-agnostic explorer that taps whatever is on screen, avoiding destructive
  labels and anything that looks like a price. Finds the flows nobody scripted. It prefers what it has
  tapped least this run (`Prefers untried`, 0.8 by default), because a uniform random walk wedges in
  any two-room flow: one live button on the home screen, one non-price button in the room it opens,
  and it ping-pongs between them until the budget is gone.
- **Circle detection.** A bot walking the same two or three screens for ever — open the store, close
  the store, out of lives and nothing else to press — is a finding naming the actions it kept
  alternating between, plus a screenshot, rather than a run that reports "budget spent". The dead-end
  detector cannot see it: every beat of that cycle changes the screen. Tuned with `"loopAfter"` on
  `runBot` (0 turns it off) and `BotRunOptions.LoopWindow` / `LoopStates`.
- **`Tools ▸ GameTestKit ▸ Bot Runner`** — turn a bot loose without writing a test. Bot runs write
  `bot-<name>.json` into the run folder: every action, the tracked values beside it, and a screenshot
  at each finding.
- **`Kobapps.CodeEditor.Editor`** — a reusable code-editing surface in its own assembly, with no
  references to anything: gutter, monospace area, autocomplete with keyboard navigation, and a
  diagnostics line. Parameterised by two small interfaces (`ICompletionSource`, `ICodeValidator`), so
  any Editor tool that needs a code editor can host it. Undo/redo is the widget's own (Ctrl+Z,
  Ctrl+Y), so it covers the edits the editor makes for you — accepting a completion, reformatting —
  which a `TextField`'s built-in history cannot see. The completion list appears under the word it is
  completing, flipping above the line when there is no room below.
- **Script editor in the main window**, built on that widget. Completions and validation both come
  from the live Editor — verbs from `StepRegistry`, state paths from `GameTestBindings`, parsing from
  the runner's own parser — so a game's custom steps appear in the dropdown the day they are written
  and the editor can never accept something the runner would reject. Ctrl+Space forces the list;
  inside an `assert` / `waitFor` it offers state and expression functions instead of verbs.
- **AI interface** — `AICommands.Catalogue`, `AICommands.Inspect` (a machine-readable snapshot of
  everything on screen, with recommended selectors and live bindings), `AICommands.Validate`, plus a
  generated `SKILL.md` documenting this project's steps, ids, scenes and bindings.
- **`GameTestKitAgent`** — the start/poll/stop API an agent drives a run through, each call returning
  JSON. `Status()` reports `stale` and `startedButNotRunning`, the two facts a report file cannot
  give while a run is in flight, and `Start` refuses when the project has compile errors instead of
  queueing a run Unity will never start.
- **`LiveStatus` heartbeat** at `Library/GameTestKit/live-status.json`, rewritten on every step
  boundary, so a caller can tell a slow run from a wedged one. **`Tools ▸ GameTestKit ▸ Live Run`**
  shows the same thing in the Editor: the running test, the current step, time since the last
  progress, and the steps taken so far.
- **Samples** — a code-built demo game with four tests, and a Unity Test Framework bridge.

[1.0.0]: https://github.com/Kobapps/GameTestKit/releases/tag/v1.0.0
