# Running in CI and on devices

## The command

```bash
Unity -batchmode -projectPath . \
      -executeMethod Kobapps.GameTestKit.Editor.GameTesterCLI.Run \
      -gtk-tags smoke \
      -gtk-report Artifacts/gametests \
      -gtk-retries 1 \
      -gtk-timeout 30
```

**Never pass `-quit`.** A GameTestKit run needs play mode, which outlives the `-executeMethod`
call; `-quit` would kill the Editor before a single test ran. The CLI exits the process itself:

| Code | Meaning |
|---|---|
| 0 | Everything passed |
| 1 | At least one test failed, errored or timed out |
| 2 | The run could not complete — a broken script, no matching tests, or play mode never started |

The CLI lives in the editor assembly, so a CI project needs
[EditorCoreKit](https://github.com/Kobapps/EditorCoreKit) in its manifest just as a developer machine
does — see the README's install section. Nothing it ships reaches a player build.

`-gtk-timeout <minutes>` is a wall-clock guard that exits with 2 rather than hanging a build agent.

### Screenshots in batch mode

Batch mode never presents a frame, so screenshots are **skipped** — with or without `-nographics`.
This is deliberate: capturing requires waiting for end-of-frame, and in batch mode that wait never
returns, which would hang the run instead of producing a picture. The run logs one line saying
screenshots are off; step timings, messages and reports are unaffected.

If failure screenshots matter to your triage — and they usually do — run the Editor normally (not
`-batchmode`) on a machine with a display, or run the suite from the GameTester window.

## Flags

| Flag | Effect |
|---|---|
| `-gtk-test <path>` | Run one script. Repeatable. |
| `-gtk-suite <path>` | Load filters and options from a `.gamesuite.json`. |
| `-gtk-filter <text>` | Substring match on test names. |
| `-gtk-tags a,b` | Run tests carrying any of these tags. |
| `-gtk-exclude-tags a,b` | Skip tests carrying any of these. |
| `-gtk-categories a,b` | Run tests in these categories, nested ones included. |
| `-gtk-exclude-categories a,b` | Skip these categories and everything under them. |
| `-gtk-report <dir>` | Where reports and screenshots go. |
| `-gtk-formats junit,json,html` | Which reports to write. |
| `-gtk-retries N` | Re-run a failing test before reporting it. |
| `-gtk-repeat N` | Run the whole selection N times. |
| `-gtk-shuffle` / `-gtk-seed N` | Randomise order; the seed is printed and stored in the report. |
| `-gtk-stop-on-failure` | Abort the batch on the first failure. |
| `-gtk-pointer touch` | Deliver gestures as touches instead of mouse input. |
| `-gtk-backend inputSystem` | Force a specific input backend. |
| `-gtk-speed N` | Multiply gesture durations. |
| `-gtk-timescale N` | Fast-forward the game. |
| `-gtk-screenshot-every-step` | A screenshot after every step (slow; good for a flaky test). |
| `-gtk-allow-log-errors` | Do not fail tests on logged errors. |
| `-gtk-isolate-devices` | Disable real hardware input during the run. |
| `-gtk-no-overlay` | Do not draw the on-screen input overlay (it is on by default, and appears in screenshots). |

## GitHub Actions

```yaml
- name: Game tests
  run: |
    "$UNITY" -batchmode -projectPath . \
      -executeMethod Kobapps.GameTestKit.Editor.GameTesterCLI.Run \
      -gtk-tags smoke -gtk-report Artifacts/gametests -gtk-timeout 30 \
      -logFile - || EXIT=$?
    exit ${EXIT:-0}

- name: Publish results
  if: always()
  uses: actions/upload-artifact@v4
  with:
    name: game-tests
    path: Artifacts/gametests      # results.junit.xml, results.json, report.html, screenshots
```

Point your JUnit reporter at `Artifacts/gametests/results.junit.xml`. Failure messages and screenshot
paths are embedded in it, so a failing check is readable without downloading the artifact.

## Sharding

Tests run sequentially inside one Editor instance; parallelism belongs at the job level. Split by
category — the folders you already organise tests into are the shards:

```yaml
strategy:
  matrix:
    shard: [Shop, Combat, Onboarding]
# …  -gtk-categories ${{ matrix.shard }}
```

A category covers everything nested under it, so `Combat` picks up `Combat/Bosses` without the
matrix having to list it. Split by tag instead (`-gtk-tags`) when your shards cut across the tree.

Each shard writes its own JUnit file, and the `classname` in it carries the category
(`GameTestKit.Combat.Bosses`), so a dashboard that merges the shards still groups them correctly.

## Running on a device

The same scripts run inside a built player, which is the only way to catch what only breaks on real
hardware — touch handling, frame budget, platform input quirks.

1. `Tools ▸ GameTestKit ▸ Copy Tests To Resources` so the scripts ship inside the build.
2. Build a development player.
3. Run it with:

```bash
MyGame.exe -gametests -gametest-tags smoke -gametest-report C:\out -gametest-quit
```

| Flag | Effect |
|---|---|
| `-gametests` | Run everything discovered. |
| `-gametest-filter <text>` / `-gametest-tags a,b` | Narrow the selection. |
| `-gametest-file <path>` | Run a specific script from disk. |
| `-gametest-report <dir>` | Output folder (defaults to the persistent data path). |
| `-gametest-repeat N` | Repeat the selection. |
| `-gametest-quit` | Quit with 0 or 1 when finished. |

Scripts placed in `StreamingAssets/GameTests/` are also discovered, so QA can add a test next to an
existing build without a rebuild. On Android and WebGL, where StreamingAssets cannot be enumerated,
use the Resources path instead.

For touch platforms add `"pointer": "touch"` to the suite, or `-gtk-pointer touch` in the Editor CLI,
so gestures are delivered as finger events rather than mouse clicks.

## Through the Unity Test Framework

Import the *Test Framework Bridge* sample and every script becomes a PlayMode NUnit test:

```bash
Unity -batchmode -runTests -testPlatform PlayMode -projectPath . \
      -testResults Artifacts/results.xml -quit
```

(`-quit` is correct here — UTF owns the play-mode lifecycle.) Use this when consolidating pipelines
matters more than per-step timings and screenshots, or run both against the same files.

## Keeping runs trustworthy

- **`failOnLogError` on.** A logged error is a defect even when the assertions passed. Add specific
  `ignoreLogs` patterns for known noise rather than turning the policy off wholesale.
- **Retries as a signal, not a fix.** `-gtk-retries 1` absorbs genuine flakiness, and the report says
  which attempt passed. A test that only ever passes on attempt 2 is telling you something.
- **Shuffle nightly.** Order-dependence between tests is invisible until it costs a release. The
  reported seed reproduces the exact order.
- **Keep the artifacts.** The screenshot at the moment of failure answers most triage questions
  before anyone opens the Editor.
