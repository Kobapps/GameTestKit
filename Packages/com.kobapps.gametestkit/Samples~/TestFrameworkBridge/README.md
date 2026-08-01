# Unity Test Framework bridge

Makes every `.gametest.json` script appear as a PlayMode test in the Test Runner window and in
`-runTests`, so a team that already has a UTF pipeline gets GameTestKit flows through the same
command, the same report and the same CI job.

## Install

Import the sample, then move both files into your own test folder — for example
`Assets/Tests/PlayMode/`. That is all; scripts are discovered automatically.

Alternatively, leave them here and add the package to your project manifest's `testables`:

```json
"testables": [ "com.kobapps.gametestkit" ]
```

## Run

```bash
Unity -batchmode -runTests -testPlatform PlayMode -projectPath . \
      -testResults Artifacts/results.xml -quit
```

Or open **Window ▸ General ▸ Test Runner ▸ PlayMode** and press Run All.

## You do not need this

The GameTester window and `GameTesterCLI.Run` run the same tests without the Test Framework, and give
you screenshots, an HTML report and per-step timings that UTF's own report cannot express. Use the
bridge when consolidating pipelines matters more than the extra detail — or use both: they read the
same script files.
