# GameTestKit — End-to-End Game Testing for Unity

[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black.svg)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](Packages/com.kobapps.gametestkit/LICENSE.md)

This repository is a Unity project that hosts the **GameTestKit** package. The package — and its
documentation — lives in:

**[`Packages/com.kobapps.gametestkit/`](Packages/com.kobapps.gametestkit/README.md)**

GameTestKit is an end-to-end game testing framework for Unity 6: tests drive real gameplay with
real simulated input (clicks, taps, drags, keys, gamepad) at the device level, are written as
`.gametest.json` scripts or with a C# fluent API, run in the Editor / CI / a built player, and
produce JUnit, JSON and HTML reports with failure screenshots. It also ships a first-class AI
interface so agents can discover, author, validate and run tests on their own.

## Install into your game

```
https://github.com/Kobapps/GameTestKit.git?path=/Packages/com.kobapps.gametestkit
```

## Working in this repository

Open the repository root as a Unity project (Unity 6000.0+). The package is embedded, so edits to
`Packages/com.kobapps.gametestkit/` are live. The project manifest pulls in
[EditorCoreKit](https://github.com/Kobapps/EditorCoreKit), which the editor tooling is built on.

- **Tests** — `Window ▸ General ▸ Test Runner ▸ EditMode`. The package is listed in the project
  manifest's `testables`, so its tests appear automatically.
- **Try it** — Package Manager ▸ GameTestKit ▸ Samples ▸ *Demo Game & Tests*, then
  `Tools ▸ GameTestKit ▸ GameTester ▸ Run All`. The demo builds its own UI, so no scene setup is
  needed.

Documentation: [README](Packages/com.kobapps.gametestkit/README.md) ·
[script format](Packages/com.kobapps.gametestkit/Documentation~/script-format.md) ·
[CI and devices](Packages/com.kobapps.gametestkit/Documentation~/ci.md) ·
[changelog](Packages/com.kobapps.gametestkit/CHANGELOG.md)
