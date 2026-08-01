# Changelog

All notable changes to GameTestKit are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
