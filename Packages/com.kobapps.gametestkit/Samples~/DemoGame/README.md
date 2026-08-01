# Demo Game & Tests

A tiny game and four tests that exercise every part of GameTestKit: clicks, a drag, text entry,
assertions on both the screen and game state, setup via bindings, and teardown.

## Run it

1. Import this sample (Package Manager ▸ GameTestKit ▸ Samples ▸ Import).
2. Open any scene — even an empty one. The demo builds its own canvas and EventSystem, so it needs
   no scene asset and no Build Profiles entry.
3. `Tools ▸ GameTestKit ▸ GameTester`, then **Run All**.

Or from the command line:

```bash
Unity -batchmode -projectPath . -executeMethod Kobapps.GameTestKit.Editor.GameTesterCLI.Run -gtk-tags demo -gtk-report Artifacts/demo
```

To play with it by hand, add the `DemoGameBootstrap` component to an empty GameObject and press Play.

## What to look at

`DemoGame.cs` is the interesting file, and it is short. Three things in it are what make the tests
above possible — copy them into your own game:

**`TestId.Assign(shopButton, "shop_button", "Opens the shop.")`** — every element a test touches has
a stable id. Tests say `id:shop_button` and keep working when the hierarchy is rearranged or the
button is relabelled for another language.

**Bindings** — `demo.gold` is readable from an assertion, `demo.grantGold` is callable from a step.
This is how a test sets up a state in one line instead of clicking through five screens to reach it,
and how it asserts on what the game actually believes rather than on a formatted string.

**Real uGUI interactions** — the card implements `IBeginDragHandler`/`IDragHandler`/`IEndDragHandler`
and the slot implements `IDropHandler`. Nothing about the drag is special-cased for tests: the
simulated pointer crosses the drag threshold, the handlers fire in order, and the drop lands.

## The tests

| File | What it proves |
|---|---|
| `buy-a-sword.gametest.json` | Money is deducted and the UI keeps up |
| `cannot-overspend.gametest.json` | A purchase that should fail charges nothing and grants nothing |
| `drag-a-card.gametest.json` | A full drag gesture reaches the drop handler |
| `type-a-name.gametest.json` | Text input arrives as real character events |
| `demo.gamesuite.json` | Runs the four together with a retry and HTML output |

Note what `cannot-overspend` does in `setup`: it spends the gold *through the shop* rather than
setting a field. Setup that goes through the game finds bugs that setup which pokes at internals
never will — while `demo.grantGold` stays available for the cases where clicking through would only
add minutes and no coverage.
