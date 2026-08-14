# Testing what the game *emits*

Most of GameTestKit is about what the game shows. This part is about what it **sends** — analytics,
server calls, IAP receipts, ad callbacks, remote-config fetches, save writes. They are all the same
shape: a name, a payload, and what each sink did with it. One set of verbs covers all of them, which
is why the kit says *event* rather than *analytics*.

Telemetry is worth testing for a reason that does not apply to the rest of the game: **nothing tells
you when it breaks.** A button that stops working produces a bug report the same day. An event that
starts sending `"win"` where it used to send `"Win"` produces a dashboard that quietly reads zero,
and the first person to notice is an analyst three weeks later asking why win rate fell off a cliff.

## 1. Feed the log

The kit does not know how your game emits. Give it one line wherever your own event bus already
reports:

```csharp
using Kobapps.GameTestKit;
using UnityEngine;

public static class AnalyticsTestBridge
{
    // SubsystemRegistration, not AfterSceneLoad: install, first-session and the loading chain all
    // fire before a test's first step can run, and there is no later moment to observe them.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Install()
    {
        AnalyticsHub.Recorded += captured => TestEventLog.Record(
            captured.EventName,
            captured.Properties,
            // Optional. Without it you can still assert payloads, just not reach.
            captured.Deliveries.ConvertAll(d => new TestEventDelivery(d.Provider, d.Delivered, d.Error)));
    }
}
```

That is the whole integration. `TestEventLog` is thread-safe — an ad or attribution SDK delivering on
its own thread is exactly the event worth recording — and records nothing in a release build unless
you set `TestEventLog.Enabled = true`.

## 2. Write the case

```json
{
  "name": "Level_End says a level was won",
  "category": "Analytics",
  "tags": ["analytics"],

  "steps": [
    { "eventCase": "Level_End", "case": "win",
      "companions": ["Level_Start", "Popup_Shown"] },

    { "enterLevel": 1 },
    { "playLevel": "intuitive" },
    { "waitForEvent": "Level_End", "within": 30 },

    { "expectEvent": "Level_End",
      "count": 1,
      "props": {
        "Level_ID":     1,
        "Level_Result": "Win",
        "Fail_Reason":  "None",
        "Duration":     ">0",
        "Moves_Used":   ">=1",
        "Boosters":     "*",
        "Debug_Flag":   "!"
      },
      "delivered":    ["Mixpanel"],
      "notDelivered": ["Singular"],
      "screenshot":   true },

    { "expectOrder": ["Level_Start", "Level_End"] },
    { "eventProof": true }
  ]
}
```

`"count": 1` is doing real work. An attempt that closes twice silently doubles your win rate, and it
is invisible to any check that only asks whether the event fired.

## The verbs

| Verb | What it does |
|---|---|
| `eventCase` | Opens a case: marks the window and names the event being proved. |
| `waitForEvent` | Waits until an event has fired. Use instead of sleeping for long enough. |
| `expectEvent` | Waits for it, then asserts payload, count and reach. |
| `expectNoEvent` | Asserts it did *not* fire — how a retired event is kept retired. |
| `expectOrder` | Asserts several fired in order, ignoring whatever came between. |
| `expectOnlyExpectedEvents` | Fails on anything in the window the case did not declare. |
| `eventInvariants` | The session-wide sweep (below). |
| `eventProof` | Closes the case and writes its record beside the report. |

Everything after `eventCase` is scoped to that case's window, so a suite running cases back to back
never sees the previous one's events.

## Property matchers

The value in `props` is a matcher, not just a literal. Each form exists because a real defect looks
like it:

| Matcher | Means |
|---|---|
| `1`, `"Win"`, `true` | equality (strings case-insensitively, numbers within an epsilon) |
| `"*"` | present and non-null — for a value that is real but not predictable |
| `"!"` | absent or null — for a property that must **not** be on this event |
| `">0"` `">=1"` `"<10"` `"<=3"` `"!=0"` | a numeric bound. A duration of zero is also "a number" |
| `"~^Level_"` | regex |
| `["Win","Fail","Quit"]` | one of |
| `"type:number"` | `number`, `string`, `bool` or `list` |
| `"@const:My.Telemetry.RESULT_*"` | one of the constants that class declares |

The last one is the one worth adopting deliberately. A test holding its own copy of `"Win"` passes
happily after someone renames the constant to `"Victory"` — it is asserting against a literal that no
longer means anything. `@const:` asserts against the vocabulary itself, so the rename fails the test
where it should.

## Session invariants

Some defects are invisible to any single event's payload:

```json
{ "eventInvariants": true,
  "pairs": [["Level_Start", "Level_End"], ["Session_Start", "Session_End"]],
  "requiredProps": ["Session_Id", "Build_Number"],
  "exempt": ["App_Install"],
  "sequenceProperty": "Event_Number" }
```

- **Gaps** in the sequence — the only in-band evidence that something was dropped.

  Set `sequenceProperty` to whatever your game stamps its per-session counter as, or this check
  cannot find anything. `TestEventLog` numbers what it *receives*, so its own numbering is
  contiguous by construction; a counter written by the game at emit time is the one that leaves a
  hole when an event never made it to the log. That property is also excluded from the duplicate
  comparison automatically — a value that differs on every event would otherwise make every payload
  unique and quietly turn duplicate detection off. Use `ignoreInDuplicates` for any others like it,
  such as a timestamp.
- **Pairing** — two `Level_Start`s with no end between them is a phantom attempt, and every
  attempt-grain metric doubles when it happens.
- **Duplicates** — the same payload twice within 250 ms is a double-subscribe, not a player doing
  something twice.
- **Required properties** — a session id missing from one event in fifty.

## What a failure looks like

Failures name what actually happened rather than only what did not:

```
no 'Level_End' was recorded within 30.0s. What did fire: Level_Start, Popup_Shown, Booster_Used
```

```
'Level_End' fired, but it did not say what was expected:
  • Level_Result: expected "Win", got win — the values differ
  • Duration: expected ">0", got 0 — 0 is not > 0
  • 'Level_End' should have reached Mixpanel, but it was refused: no consent
```

## The proof

`eventProof` writes an `event-proof-*.json` beside the test's artifacts and puts a panel in the HTML
report: the payload **in full**, per-property verdicts marked in place, which sinks took it, and the
screenshot of the moment it fired.

Rendering the whole payload rather than only the checked properties is deliberate. A property nobody
asserted is exactly where an unnoticed regression lives, and a reviewer skimming the report is the
cheapest chance anyone has of spotting it.

## Fixtures

Analytics cases usually need the same reset before each one. Rather than copying it into every file,
declare it once in the suite:

```json
{
  "name": "Analytics",
  "categories": ["Analytics"],
  "beforeEach": [
    { "call": "toMenu" },
    { "waitFor": "game.inLevel == false", "timeout": 15 },
    { "call": "resetProgress" }
  ],
  "afterEach": [ { "call": "closeAllPopups" } ]
}
```

`beforeEach` runs before each test's own `setup`; a failure there is reported as an *error*, because
the test never ran. `afterEach` always runs and its failures are logged, not fatal — as with
`teardown`. `GameTesterSettings ▸ Fixtures` sets the same thing project-wide, which is what applies
to runs started from the GameTester window.

## Testing the tests

`TestEventLog` is a plain static, so a fixture can drive it directly from an EditMode test:

```csharp
TestEventLog.Clear();
TestEventLog.Record("Level_End", new Dictionary<string, object> { { "Result", "Win" } });

Assert.That(TestEventLog.CountOf("Level_End"), Is.EqualTo(1));
```
