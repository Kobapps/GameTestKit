# The `.gametest.json` format

The complete reference. For the generated, project-specific version — including any step verbs your
game registers — run `Tools ▸ GameTestKit ▸ Print Step Catalogue`, or install the AI skill.

## File shape

```json
{
  "name": "Buying a sword spends gold",
  "description": "Why this flow matters.",
  "tags": ["smoke", "shop"],

  "scene": "Shop",
  "timeout": 120,
  "repeat": 1,
  "retries": 1,
  "skip": false,
  "skipReason": "",

  "setup":    [],
  "steps":    [],
  "teardown": []
}
```

| Field | Meaning |
|---|---|
| `name` | Used for filtering, reporting and artifact folders. Defaults to the file name. |
| `tags` | Suite membership. Matching is case-insensitive. |
| `scene` | Loaded before `setup`. Omit to test whatever is already open. |
| `timeout` | Wall-clock budget for the whole test, in unscaled seconds. |
| `repeat` | Runs `steps` this many times inside one test — soak and flake hunting. |
| `retries` | Re-runs the whole test after a failure before reporting it. |
| `skip` | Reports the test as skipped with `skipReason`. |

A bare array of steps is also a valid file; the name then comes from the file name
(`buy-a-sword.gametest.json` → "buy a sword").

`//` and `/* */` comments and trailing commas are accepted. Parse errors report line and column.

### Phases

- **`setup`** prepares the world. A failure here is reported as an *error* — the test never got to
  run — which keeps a broken fixture from looking like a product bug.
- **`steps`** is the test. A failure here is a *failure*.
- **`teardown`** always runs, including after a failure. Its own failures are logged, not fatal.

## Steps

Every step is an object whose key is the verb. Every step also accepts:

| Field | Meaning |
|---|---|
| `timeout` | Seconds this step may take. Defaults to the run's step timeout (15s). |
| `label` | Overrides the description shown in reports. |
| `continueOnFailure` | Record the failure and keep going — a soft assertion. |

### Input

```json
{ "click": "id:play_button" }
{ "click": "id:slider", "at": [0.9, 0.5] }          // normalised point inside the element
{ "click": "#Card", "button": "right", "clicks": 2 }
{ "tap": "text:\"Play\"" }                           // alias of click
{ "doubleClick": "#InventorySlot" }
{ "hold": "id:charge", "seconds": 1.5 }
{ "hover": "#ItemIcon" }
{ "drag": "id:card_3", "to": "id:slot_1", "duration": 0.5 }
{ "drag": "#Card", "to": "screen:640,360" }
{ "swipe": "left", "from": "#Carousel", "distance": 400, "duration": 0.15 }
{ "scroll": -4, "over": "#ShopList" }                // negative scrolls down, in wheel notches
{ "type": "Ada", "into": "id:name_field", "clear": true }
{ "press": "space", "repeat": 3, "hold": 0.1 }
{ "keyDown": "w" }  …  { "keyUp": "w" }
{ "gamepad": "south", "hold": 0.1 }
{ "stick": "left", "value": [0, 1], "seconds": 1.2 }
```

`click` waits until the target exists, is visible, is interactable and is not covered by anything
before pressing. `"force": true` skips only the covered check — use it to *test* that a blocker
blocks, not to work around one.

### Flow

```json
{ "wait": 0.5 }                                      // prefer waitFor
{ "waitFor": "player.gold == 40", "timeout": 5 }
{ "waitForVisible": "id:reward_popup", "interactable": true }
{ "waitForGone": "#LoadingSpinner", "timeout": 30 }
{ "waitForScene": "Gameplay" }
{ "repeat": 3, "steps": [ … ] }
{ "group": "Buy a sword", "steps": [ … ] }           // names a block in the report
{ "log": "About to open the shop" }
{ "screenshot": "shop-open" }
```

### Scene and game state

```json
{ "loadScene": "MainMenu", "additive": false }
{ "unloadScene": "PauseMenu" }
{ "call": "grantGold", "args": [500] }
{ "timeScale": 4 }
```

`call` invokes an action registered with `GameTestBindings`. If the action returns an `IEnumerator`
the runner awaits it before moving on.

### Assertions

```json
{ "assert": "player.gold == 40", "message": "The sword costs 60" }
{ "assert": "demo.items == 1", "retryFor": 2 }       // keep re-checking for 2s first
{ "assertVisible": "id:shop_panel" }
{ "assertHidden": "id:shop_panel" }
{ "assertExists": "#Boss" }
{ "assertMissing": "#Boss" }
{ "assertInteractable": "id:buy" }
{ "assertDisabled": "id:buy" }
{ "assertText": "id:gold_label", "equals": "40" }
{ "assertText": "id:status", "contains": "Bought" }
{ "assertText": "id:timer", "matches": "^\\d\\d:\\d\\d$" }
{ "expectLog": "Not enough gold", "within": 5 }
```

`assertText` reads uGUI `Text`, TextMeshPro and input fields, and finds a label nested inside a
button — so `{"assertText": "id:buy_button", "contains": "Sword"}` works without addressing the
child text object.

`expectLog` declares that the game is *supposed* to log an error here, so the run's fail-on-error
policy allows it. Without it, any `Debug.LogError` during a step fails the test.

## Selectors

| Syntax | Matches |
|---|---|
| `id:play_button` | a `TestId` component |
| `#PlayButton` | GameObject by exact name (case-insensitive fallback) |
| `Canvas/Menu/Play` | hierarchy path; a trailing part is enough |
| `text:Play` | visible label contains "Play" |
| `text:"Play"` | visible label is exactly "Play" |
| `tag:Enemy` | Unity tag |
| `type:Button` | has that component (short or full type name) |
| `pos:0.5,0.5` | normalised screen point |
| `screen:640,360` | absolute pixels |
| `world:1,0,3` | world position through the main camera |

Add `[n]` for the n-th match (zero-based) and `>>` to scope to descendants:
`id:shop_panel >> text:Buy`.

Inactive objects are found; "found" is not the same as "visible", and steps that need visibility say
so in their failure message. A `text:` match resolves to the nearest clickable ancestor, because that
is what a player clicks.

Point selectors (`pos:`, `screen:`, `world:`) name a location, not an object, so they work with steps
that take a target but not with lookups like `exists()` or `assertVisible`.

## Expressions

Used by `assert` and `waitFor`.

- **Operators**: `and or not` (or `&& || !`), `== != > >= < <=`, `contains startsWith endsWith
  matches`, `+ - * /`, parentheses.
- **Functions**: `exists(sel)`, `count(sel)`, `visible(sel)`, `interactable(sel)`, `blocked(sel)`,
  `text(sel)`, `label(sel)`, `sceneLoaded(name)`, `abs`, `min`, `max`, `round`, `len`.
- **Built-in values**: `scene`, `time`, `realtime`, `timeScale`, `frameCount`, `fps`,
  `screen.width`, `screen.height`.
- **Bindings**: anything registered with `GameTestBindings`.

Selectors inside expressions must be quoted: `visible('id:shop_panel')`.

```
player.gold >= 100 and visible('id:shop_panel') and not blocked('id:buy')
```

## Suites — `.gamesuite.json`

```json
{
  "name": "Smoke",
  "description": "Must pass before anything ships.",
  "tags": ["smoke"],
  "excludeTags": ["nightly"],
  "include": ["Assets/GameTests/checkout"],
  "options": {
    "retries": 1,
    "repeat": 1,
    "stopOnFirstFailure": false,
    "shuffle": false,
    "seed": 0,
    "stepTimeout": 15,
    "locatorTimeout": 5,
    "inputSpeed": 1,
    "timeScale": 0,
    "pointer": "mouse",
    "backend": "auto",
    "isolateRealDevices": false,
    "showInputOverlay": true,
    "failOnLogError": true,
    "ignoreLogs": ["Shader .* not supported"],
    "screenshotOnFailure": true,
    "screenshotEveryStep": false,
    "collectPerformance": true,
    "artifactRoot": "",
    "reportFormats": ["junit", "json", "html"]
  }
}
```

`pointer` is `mouse` or `touch`; `backend` is `auto`, `inputSystem` or `eventSystem`. `seed` of 0
picks a fresh seed and reports it, so a failure found by `shuffle` can be reproduced exactly.

## The input overlay

`showInputOverlay` (on by default) draws what the virtual user is doing on top of the running game:
a yellow ring that follows the simulated pointer, a ripple wherever it taps, the path of every drag,
and a caption strip carrying the current step and the text being typed, character by character as
the game receives it. It answers the two questions a log cannot — *where did it actually click* and
*what did the field actually get* — and because IMGUI is part of the presented frame, it is in the
failure screenshot too.

Toggling it:

- **F9** while the game runs (`InputOverlay.ToggleHotkey` changes the key). This reads the real
  keyboard, so it does nothing under `isolateRealDevices` — use the menu item instead.
- **`Tools ▸ GameTestKit ▸ Input Overlay`**, or the toggle on the window's Options page.
- **`"showInputOverlay": false`** in a suite, or `-gtk-no-overlay` on the CLI, for one run.
- **`InputOverlay.Enabled`** from code. Custom steps can add their own markers and captions with
  `InputOverlay.Ripple(point)` and `InputOverlay.Note(text)`.

Nothing is drawn — and no overlay object is created — in `-batchmode` or under `-nographics`. Turn
it off for a run whose screenshots are compared against reference images.
