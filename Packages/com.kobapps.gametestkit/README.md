# GameTestKit

End-to-end game testing for Unity 6. Tests drive **real gameplay with real input** — device-level
clicks, taps, drags, swipes, keystrokes and gamepad events that enter the engine where the operating
system would put them. Nothing in your game knows a test is running.

```json
{
  "name": "Buying a sword spends gold",
  "tags": ["smoke", "shop"],
  "scene": "Shop",
  "setup": [ { "call": "grantGold", "args": [100] } ],
  "steps": [
    { "click": "id:shop_button" },
    { "waitForVisible": "id:shop_panel" },
    { "click": "text:\"Sword\"" },
    { "waitFor": "player.gold == 40" },
    { "assertText": "id:gold_label", "equals": "40" }
  ]
}
```

- **Real input, not a shortcut.** A click is a pointer that travels, presses, holds a beat and
  releases — through `InputAction`s, `PlayerInput`, interactions, the UI module, drag thresholds and
  `Mouse.current`. Frameworks that call `button.onClick.Invoke()` pass while the shipped game is
  broken; this one does not.
- **Selectors with implicit waiting.** `id:shop_button`, `text:"Play"`, `type:Button[2]`,
  `id:shop >> text:Buy`. Every lookup waits for the element to exist, be visible, be interactable and
  be *un-covered* before touching it — so animated UI and async loads don't cause flakes.
- **Scripts, not code.** `.gametest.json` files need no compile step, read in review, and can be
  written by a teammate or an agent. A C# fluent API is there when a test needs real logic.
- **Batch anywhere.** The GameTester window, a CI command line, a built player on a device, or the
  Unity Test Framework — same script files, same results.
- **Reports that explain themselves.** JUnit XML, JSON and a self-contained HTML page, with per-step
  timings, failure screenshots, captured log errors and frame-time statistics.
- **Built for agents.** Four commands let an AI learn the vocabulary, look at the screen, validate a
  script and run it — plus a generated `SKILL.md` describing *this* project's steps, ids and bindings.

---

## Install

**Package Manager ▸ + ▸ Add package from git URL…**

```
https://github.com/Kobapps/GameTestKit.git?path=/Packages/com.kobapps.gametestkit
```

or in `Packages/manifest.json`:

```json
"com.kobapps.gametestkit": "https://github.com/Kobapps/GameTestKit.git?path=/Packages/com.kobapps.gametestkit"
```

Requires Unity 6 (6000.0+). `com.unity.ugui` and `com.unity.inputsystem` come in as dependencies.

### Required dependency — EditorCoreKit

GameTestKit's editor tooling — the GameTester window, the recorder and the inspectors — is built on
[EditorCoreKit](https://github.com/Kobapps/EditorCoreKit), so install it alongside GameTestKit:

```json
{
  "dependencies": {
    "com.kobapps.editorcorekit": "https://github.com/Kobapps/EditorCoreKit.git?path=/Packages/com.kobapps.editorcorekit#v2.0.0"
  }
}
```

UPM does not resolve git dependencies transitively, which is why this is a line in your manifest
rather than one in GameTestKit's. Without it the editor assembly does not compile — the **runtime**
has no dependency on it at all, and nothing EditorCoreKit ships is included in a build. CI runs
(`GameTesterCLI.Run`) still need it, because the CLI lives in the editor assembly.

### Requirements

- Unity **6000.0** or newer.
- **EditorCoreKit** for the editor tooling (see above). No runtime dependency on it.
- **Active Input Handling set to “Input System Package”** (Project Settings ▸ Player). Device-level
  injection cannot be seen by the legacy `UnityEngine.Input` API — a game that reads `Input.GetKey` is
  served by the old backend and will not see simulated input. UI-only flows still work through the
  EventSystem backend either way.

---

## 60-second start

1. `Tools ▸ GameTestKit ▸ GameTester` and press **New Test** — you get a template in
   `Assets/GameTests/`.
2. Add a **Test Id** component to the buttons the test touches (`GameObject ▸ Add Component ▸
   GameTestKit ▸ Test Id`), or just use `text:"Play"` to start with.
3. Press **Run All**. Unity enters play mode, the test drives the game, and results appear in the
   window with screenshots and an HTML report.

Prefer to see it working first? Import the **Demo Game & Tests** sample and run it — it needs no
scene setup at all.

---

## Writing tests

A test is a JSON object. The **key is the verb**; the rest of the object are its parameters.

```json
{
  "name": "Human-readable name",
  "description": "What this flow proves.",
  "tags": ["smoke"],
  "scene": "MainMenu",
  "timeout": 120,
  "retries": 1,

  "setup":    [ /* prepare the world */ ],
  "steps":    [ /* the test itself   */ ],
  "teardown": [ /* always runs       */ ]
}
```

A bare array of steps is also a valid file — the name comes from the filename.

### Categories — keeping a big suite navigable

Put a script in a folder and that folder is its category. Nothing to declare, nothing to keep in
sync:

```
Assets/GameTests/
  smoke.gametest.json           → no category
  Shop/
    buy-a-sword.gametest.json   → "Shop"
    Checkout/
      pay.gametest.json         → "Shop/Checkout"
```

Categories nest, so anything aimed at `Shop` also covers `Shop/Checkout`. They show up everywhere a
long list used to: the GameTester window groups and folds by them and can run one with a click, the
HTML report gets a heading per category, the JUnit `classname` becomes `GameTestKit.Shop.Checkout`
so CI dashboards group correctly, and `-gtk-categories Shop` shards a CI matrix along the folders you
already have.

Move a test by moving the file — or let the window do it: right-click a test ▸ **Move to**, or use
the category button in the detail pane. **Categories ▾** in the list toolbar creates one.

Categories and tags answer different questions and filter independently. A test sits in exactly one
category and carries any number of tags: categories mirror the game's structure (`Shop`,
`Combat/Bosses`), tags mark sets that cut across it (`smoke`, `nightly`). Asking for both runs the
tests that satisfy both.

Set `"category": "Shop"` in the file only when the folder cannot say it — an imported package sample,
or a script mirrored into `Resources` for a player build. An explicit value always wins.

### Testing what the game emits — analytics and friends

The rest of the kit asserts what the game *shows*. These verbs assert what it **sends**: analytics
events, server calls, IAP receipts, ad callbacks, save writes — all the same shape, so the kit says
*event* rather than *analytics*.

This matters because telemetry breaks silently. A broken button gets a bug report the same day; an
event that starts sending `"win"` where it sent `"Win"` gets noticed by an analyst three weeks later
wondering why win rate fell off a cliff.

Give the kit one line wherever your event bus already reports:

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
private static void Install() =>
    AnalyticsHub.Recorded += e => TestEventLog.Record(e.EventName, e.Properties);
```

Then assert on it:

```json
{ "eventCase": "Level_End", "case": "win" },
{ "playLevel": "intuitive" },
{ "expectEvent": "Level_End",
  "count": 1,
  "props": {
    "Level_Result": "@const:My.Telemetry.RESULT_*",
    "Duration":     ">0",
    "Fail_Reason":  "None",
    "Debug_Flag":   "!"
  },
  "delivered":    ["Mixpanel"],
  "notDelivered": ["Singular"] },
{ "expectOrder": ["Level_Start", "Level_End"] },
{ "eventProof": true }
```

`"count": 1` catches the attempt that closes twice and silently doubles your win rate — invisible to
any check that only asks whether the event fired. `"@const:"` asserts against the vocabulary your
game declares rather than a copied literal, so renaming the constant fails the test instead of
quietly passing it. `eventInvariants` adds the session-wide checks no single payload can reveal:
sequence gaps, unbalanced `Level_Start`/`Level_End` pairs, duplicate sends.

`eventProof` writes the evidence — the payload in full, per-property verdicts, which sinks took it,
and the screenshot of the moment — beside the report and into it.

Full reference: [Documentation~/events.md](Documentation~/events.md).

### The steps

| | |
|---|---|
| **Input** | `click` (`tap`), `doubleClick`, `hold`, `move` (`hover`), `drag`, `swipe`, `scroll`, `type`, `press` (`key`), `keyDown`, `keyUp`, `gamepad`, `stick` |
| **Flow** | `wait`, `waitFor`, `waitForVisible`, `waitForGone`, `waitForScene`, `repeat`, `group`, `log`, `screenshot` |
| **Scene & state** | `loadScene`, `unloadScene`, `call`, `timeScale` |
| **Assertions** | `assert`, `assertVisible`, `assertHidden`, `assertExists`, `assertMissing`, `assertInteractable`, `assertDisabled`, `assertText`, `expectLog` |

Every step also accepts `timeout`, `label` and `continueOnFailure`. `Tools ▸ GameTestKit ▸ Print
Step Catalogue` prints them all with parameters and examples — including any your game registers.

### Selectors

| Syntax | Matches |
|---|---|
| `id:play_button` | a `TestId` component — **prefer this** |
| `#PlayButton` | GameObject by exact name |
| `Canvas/Menu/Play` | hierarchy path (a trailing part is enough) |
| `text:Play` | visible label contains "Play" |
| `text:"Play"` | visible label is exactly "Play" |
| `tag:Enemy` | Unity tag |
| `type:Button` | has that component (short or full type name) |
| `pos:0.5,0.5` | normalised screen point |
| `screen:640,360` | absolute pixels |
| `world:1,0,3` | world position through the main camera |
| `type:Button[2]` | the third match |
| `id:shop >> text:Buy` | search only inside an earlier match |

A `text:` match resolves to the thing a player would actually click — the button, not the letters
inside it.

### Expressions

`assert` and `waitFor` take an expression over game state and the screen:

```
player.gold >= 100 and visible('id:shop_panel') and not blocked('id:buy')
```

Operators `and or not == != > >= < <= contains startsWith endsWith matches + - * /`; functions
`exists count visible interactable blocked text label sceneLoaded abs min max round len`; built-in
values `scene time timeScale fps screen.width screen.height`.

### C# instead

```csharp
var test = TestBuilder.Create("Buy a sword")
    .Tag("smoke").Scene("Shop")
    .Call("grantGold", 100)
    .Click("id:shop_button")
    .WaitForVisible("id:shop_panel")
    .Click("text:\"Sword\"")
    .AssertText("id:gold_label", equals: "40")
    .Build();

yield return GameTester.RunSingleAsync(test);
```

Use it when a test needs real logic. For plain flows the JSON is better: no recompile, and anyone
can read it.

---

## Making your game testable

Two small things turn brittle tests into stable ones.

**`TestId`** — put it on anything a test touches and give it a stable id. Names get renamed, paths
get reorganised, labels get localised; ids survive all three. Runtime-spawned objects register
themselves, so no scene scan is needed to find them.

**Bindings** — the sanctioned bridge into the game:

```csharp
[RuntimeInitializeOnLoadMethod]
static void RegisterTestHooks()
{
    GameTestBindings.BindValue("player.gold", () => Player.Instance.Gold, "Soft currency");
    GameTestBindings.BindAction("grantGold",  args => Player.Instance.Gold += Convert.ToInt32(args[0]));
    GameTestBindings.BindCoroutine("loadSave", args => SaveSystem.LoadRoutine((string)args[0]));
}
```

or declaratively:

```csharp
[GameTestBinding("player.gold", Description = "Soft currency")]
public static int Gold => Player.Instance.Gold;
```

Values are readable from expressions; actions are callable with `call` and awaited if they return an
`IEnumerator`. Use them to *reach* a state quickly — then assert on what the player can see.

### Custom steps

```csharp
[GameTestSteps]                      // discovered by reflection, so the verb exists in the Editor
static void RegisterSteps()          // window and the validator too — not just inside play mode
{
    StepRegistry.Register(new StepDefinition { /* … */ });
}
```

```csharp
StepRegistry.Register(new StepDefinition
{
    Key = "castSpell",
    Summary = "Casts a spell by id.",
    Example = "{ \"castSpell\": \"fireball\" }",
    Factory = json => new CastSpellStep { Spell = json["castSpell"].AsString() },
});
```

The verb is now usable in JSON, appears in the catalogue, and is documented in the generated AI skill.

---

## Running

**In the Editor** — `Tools ▸ GameTestKit ▸ GameTester`. A **Tests** page lists everything discovered
as a folding tree of categories, each row carrying its last verdict — ✓, ✗, or ○ for not yet run —
and each category header carrying the verdict of everything underneath it, so a folded group is still
readable. **▶** on any row runs that test or that whole category. A **Results** page expands each
test into its steps, messages and screenshots; an **Options** page sets pointer mode, backend, input
speed, retries, shuffle and screenshot policy for runs started here. Turn input speed up to watch a
test drive the game in slow motion.

Right-clicking a test offers **Select script in Project**, **Open script**, **Move to** another
category, and **Delete script** — the delete moves the file to the recycle bin after naming what it
is about to remove, so a mis-click is recoverable. The same three are buttons in the inspector.

Selecting a test opens its inspector beside the list, in two tabs:

- **Overview** — steps, scene, tags, timeout, the category (with the button that moves it), and the
  last result with its failure message and screenshots.
- **Script** — the `.gametest.json` itself, edited in place, with completions generated from the live
  step registry and your game's own bindings, parse errors marked as you type, **Format**, and
  **▶ Run this**. Unsaved edits survive switching tabs, and switching to another test asks before
  leaving them behind.

The list is the file picker: there is no separate editor page to keep in sync with it.

**Watching the input** — the on-screen overlay is on by default: a yellow ring follows the simulated
pointer, a ripple marks every tap, drags draw their path, and a caption strip in the corner shows the
current step plus the text being typed, character by character as the game receives it. It answers
the two questions a log cannot — *where did it actually click*, and *what did the field actually
get* — and since it is part of the presented frame, it is in the failure screenshots too. Hide it
with **F9**, `Tools ▸ GameTestKit ▸ Input Overlay`, the Options page, `"showInputOverlay": false` in
a suite, or `-gtk-no-overlay`. Nothing is drawn in `-batchmode`.

**Watching a run** — `Tools ▸ GameTestKit ▸ Live Run` shows the run as it happens: which test, which
step, how long since anything moved, and the steps taken so far. It reads the same heartbeat the agent
API polls, so a human and an agent never disagree about what the run is doing — and it says **STUCK?**
rather than leaving you guessing when a step stops progressing.

**In CI**

```bash
Unity -batchmode -projectPath . \
      -executeMethod Kobapps.GameTestKit.Editor.GameTesterCLI.Run \
      -gtk-categories Shop -gtk-tags smoke -gtk-report Artifacts/gametests -gtk-stop-on-failure
```

Do **not** pass `-quit`: the run needs play mode, which outlives the `-executeMethod` call. The
process exits by itself with 0 (passed), 1 (failures) or 2 (the run could not complete).

Flags: `-gtk-test <path>`, `-gtk-filter <substring>`, `-gtk-tags a,b`, `-gtk-exclude-tags a`,
`-gtk-categories a,b`, `-gtk-exclude-categories a`,
`-gtk-suite <path>`, `-gtk-retries N`, `-gtk-repeat N`, `-gtk-shuffle`, `-gtk-seed N`,
`-gtk-pointer touch`, `-gtk-backend inputSystem`, `-gtk-speed N`, `-gtk-timescale N`,
`-gtk-screenshot-every-step`, `-gtk-allow-log-errors`, `-gtk-isolate-devices`, `-gtk-no-overlay`,
`-gtk-formats junit,json,html`, `-gtk-timeout <minutes>`.

**In a built player** — the real device story. Run
`Tools ▸ GameTestKit ▸ Copy Tests To Resources` once so scripts ship inside the build, then:

```bash
MyGame.exe -gametests -gametest-tags smoke -gametest-report C:\out -gametest-quit
```

Scripts dropped into `StreamingAssets/GameTests/` next to a build are picked up too, so QA can add a
test without rebuilding.

**Through the Unity Test Framework** — import the *Test Framework Bridge* sample and every script
becomes a PlayMode NUnit test, so `-runTests` and the Test Runner window pick them up.

### Suites

A `.gamesuite.json` names a set and its options:

```json
{
  "name": "Smoke",
  "tags": ["smoke"],
  "options": { "retries": 1, "pointer": "touch", "stopOnFirstFailure": false }
}
```

---

## Results

Every run writes to `<project>/GameTestKit/run-<timestamp>/` (configurable):

- `results.junit.xml` — for CI
- `results.json` — per test: status, message, every step with its own status and duration, screenshot
  paths, captured log errors, and frame-time statistics
- `report.html` — a self-contained page for humans
- `<test>/*.png` — screenshots, including one automatically at the moment of failure

Failures are written to be actionable. Not *"element not found"* but:

> Found 1 match for `id:buy` but none was usable after 5s: 'Canvas/Shop/Buy' is covered by
> 'Canvas/Tutorial/Blocker'

A logged `Debug.LogError` fails the running test by default — a defect is a defect even when every
assertion passed. Use `expectLog` for error paths you are deliberately exercising, or
`ignoreLogs`/`-gtk-allow-log-errors` for known noise.

---

## Recording

`Tools ▸ GameTestKit ▸ Recorder` — enter play mode, press Record, play the flow, press Stop. You
get a script with the most stable selector available for everything you touched. While recording,
**F8** inserts an assertion for whatever is under the pointer and **F9** inserts a screenshot step.

Treat a recording as a first draft: replace the recorded `wait` steps with `waitFor`, and add the
assertions that say what the flow is supposed to prove.

---

## For AI agents

`Tools ▸ GameTestKit ▸ AI ▸ Install Authoring Skill` writes
`.claude/skills/gametestkit-author/SKILL.md` — the workflow plus a reference generated from *this*
project: every step verb with parameters, the selector and expression languages, the scenes in the
build, the test ids that exist, and the game's bindings. Regenerate it whenever the game changes.

### Driving a run from an agent

A run lives inside play mode, which outlives the call that started it — so the shape is start, then
poll. `Kobapps.GameTestKit.Editor.GameTestKitAgent` is that API, one JSON string per call:

| Call | Answers |
|---|---|
| `Start(tags, nameFilter, paths, suitePath, retries)` | queued, or **why not** — compile errors, already playing |
| `Status()` | state, current test and step, `secondsSinceHeartbeat`, `stale`, `startedButNotRunning` |
| `Stop()` | cancels and leaves play mode |
| `LastResults()` | the finished `results.json` |

`Status()` exists to answer the question a report file cannot: **is it alive?** `stale` means nothing
has advanced for 90s; `startedButNotRunning` means play mode never picked the run up (Unity silently
refuses to enter play mode while the project has a compile error, which otherwise looks exactly like a
slow boot). The same data is written to `Library/GameTestKit/live-status.json` on every step boundary,
so a caller with file access can poll without an Editor round trip.

Four `-executeMethod` entry points, each emitting JSON to `-gtk-out <path>`:

| Command | Purpose |
|---|---|
| `AICommands.Catalogue` | Everything the script format supports, live from the registry |
| `AICommands.Inspect` | Enter play mode, load a scene, and dump every element on screen with the selector to use for it, whether it is visible, interactable, or covered — plus the live bindings |
| `AICommands.Validate` | Parse scripts and report problems without entering play mode |
| `GameTesterCLI.Run` | Run and write `results.json` |

`Inspect` is the important one: it is the difference between an agent writing selectors that exist
and an agent guessing.

---

## Settings

`Tools ▸ GameTestKit ▸ Settings` creates `Assets/Resources/GameTesterSettings.asset` (in Resources
so a player build can read it): test folders, timeouts, default backend and pointer mode, input
speed, failure policy, ignored log patterns, artifact root and report formats.

---

## How the input actually works

Two backends, chosen automatically:

**`InputSystemBackend`** creates virtual `Mouse` / `Touchscreen` / `Keyboard` / `Gamepad` devices and
queues real state events on them. Events enter the pipeline exactly where the OS would put them, so
everything downstream — actions, interactions, processors, `InputSystemUIInputModule`, drag
thresholds, `Mouse.current` — behaves as in a human session. Optionally disables real hardware
devices during a run so a stray mouse move cannot corrupt it.

**`EventSystemBackend`** raycasts the canvas stack and dispatches the enter/down/drag/up/click
messages uGUI would. It works with any input backend including the legacy Input Manager, which makes
it the portable fallback for UI flows, but it cannot reach code that reads devices directly.

Gestures are interpolated across real frames on purpose: a pointer that teleports and a press that
releases on the same frame produce behaviour a player never sees, and tests written against them
pass while the game is broken.

**Limitations, stated plainly:**

- Reads through the legacy `UnityEngine.Input` API cannot see injected events. Set Active Input
  Handling to the Input System package.
- The EventSystem backend covers pointer input only — no keys, text or gamepad.
- uGUI input fields read typed characters through the legacy input backend, so injected text events
  cannot reach them. `type` detects when its events did not land in the targeted field and feeds the
  field directly instead, with a warning. Pointer, key and gamepad input are unaffected.
- Screenshots need a presented frame, so they are skipped in `-batchmode` (waiting for end-of-frame
  there never returns). Run from the Editor when you want failure screenshots.
- Tests run sequentially in one Editor/player instance. Parallelism belongs at the CI-shard level.

---

## Samples

- **Demo Game & Tests** — a small game (menu, shop, name entry, drag-and-drop board) built from code
  so it needs no scene, plus four tests covering clicks, drags, typing and assertions.
- **Unity Test Framework Bridge** — expose every script as a PlayMode NUnit test.

## License

MIT — see [LICENSE.md](LICENSE.md).
