# Demo Game & Tests

A tiny game and eight tests that exercise every part of GameTestKit: clicks, a drag, text entry,
assertions on both the screen and game state, setup via bindings, teardown, categories, suite
fixtures, and a full set of analytics proofs.

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

**An analytics hub the tests can see into** — `DemoAnalytics.cs` is a miniature version of what every
game has: two providers, a consent switch, and super properties stamped on every event. Note that it
has **no reference to GameTestKit**. `DemoAnalyticsTestBridge.cs` is the single file that joins the
two, and it is the one worth copying:

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
private static void Install() =>
    DemoAnalytics.Sent += e => TestEventLog.Record(e.Name, e.Properties, /* deliveries */);
```

`SubsystemRegistration` is the point of it. Install, first-session and loading events fire before a
test's first step can run — subscribe any later and they are simply unobservable.

## The tests

Organised into folders, and the folder *is* the category. `Gameplay` nests, so filtering on it
picks up all three folders beneath it — that is what separates a category from a tag:

```
Tests/
  demo.gamesuite.json          → the Gameplay category
  analytics.gamesuite.json     → the Analytics category
  Gameplay/
    Shop/        buy-a-sword, cannot-overspend
    Cards/       drag-a-card
    Onboarding/  type-a-name
  Analytics/     purchase-event, declined-purchase-event,
                 consent-stops-remote-delivery, session-invariants
```

| File | What it proves |
|---|---|
| `Gameplay/Shop/buy-a-sword` | Money is deducted and the UI keeps up |
| `Gameplay/Shop/cannot-overspend` | A purchase that should fail charges nothing and grants nothing |
| `Gameplay/Cards/drag-a-card` | A full drag gesture reaches the drop handler |
| `Gameplay/Onboarding/type-a-name` | Text input arrives as real character events |
| `Analytics/purchase-event` | The payload, the count, and which sinks took it |
| `Analytics/declined-purchase-event` | The failure path reports too, and charges nothing |
| `Analytics/consent-stops-remote-delivery` | A well-formed event that must not leave the device |
| `Analytics/session-invariants` | Gaps, duplicates and required properties across a whole session |

**Every test starts with `demo.reset`.** `DemoGame.Spawn()` returns the live instance if there is
one, so a test that only calls `demo.start` inherits the previous test's gold and fails in a way
that looks like a product bug. Suite fixtures cannot cover for this: a sample has to work when
someone runs it on its own from the GameTester window, where no suite is involved.

These sample files also set `"category"` explicitly, which your own tests should **not** need to do.
An imported sample lands under `Assets/Samples/…`, not in your tests folder, so there is no sensible
folder for the kit to derive a category from — the explicit field is the documented escape hatch for
exactly that case. In your project, the folder is enough.

Note what `cannot-overspend` does in `setup`: it spends the gold *through the shop* rather than
setting a field. Setup that goes through the game finds bugs that setup which pokes at internals
never will — while `demo.grantGold` stays available for the cases where clicking through would only
add minutes and no coverage.

## What the analytics cases are showing

- **`purchase-event`** — the full shape. A literal where the case knows the answer, a bound (`">=1"`)
  where zero would be a bug, `"*"` for something real but unpredictable, `"!"` for a property that
  must *not* be there, and `"@const:…DemoTelemetry.RESULT_*"` so the assertion is against the game's
  own vocabulary rather than a copied string. `"count": 1` is the one people leave out: a purchase
  that reports twice silently doubles revenue, and no check that asks only "did it fire" can see it.
- **`declined-purchase-event`** — the same event on the failure path. A funnel can only tell "nobody
  tried" from "everybody tried and could not afford it" if the refusal is reported too.
- **`consent-stops-remote-delivery`** — `delivered` / `notDelivered`. The event is perfectly formed
  and must still not leave the device; a payload assertion cannot see this at all.
- **`session-invariants`** — what no single payload reveals. Note `"sequenceProperty":
  "Event_Number"`: gaps are looked for in the *game's* counter, because the log's own numbering has
  no holes by construction — it numbers what it receives.

`analytics.gamesuite.json` shows what suite fixtures are actually for: state that cuts across cases
and that no single test should have to know about. Here that is analytics consent — one case
withholds it, and every other case would be wrong, silently, if it leaked. Building the world each
test needs stays in that test's own `setup`. For a fixture that should apply to runs started from
the GameTester window, put it on `GameTesterSettings ▸ Fixtures` instead.
