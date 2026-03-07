# BaseMacro

`BaseMacro` is a UnityModManager mod for **A Dance of Fire and Ice (ADOFAI)**, focused on macro triggering, input simulation, asynchronous input handling, and key filtering.

## Overview

- **Automatic macro trigger** based on floor timing.
- **Two trigger paths**:
  - Direct `controller.Hit(false)` calls.
  - Keyboard simulation (SendInput / SkyHook).
- **SkyHook asynchronous mode** for high-frequency input scenarios.
- **Timing offset and hotkey adjustments** during gameplay.
- **Death key** support with configurable delay.
- **Input filtering** for sync/async key sources.
- **Chinese/English UI toggle** in settings.

## Key Settings

The following options are exposed in the mod settings panel (`Settings.cs`):

- `Macro`: Master switch for macro logic.
- `MacroKeys`: Comma-separated macro key sequence (for example: `J,K,L`).
- `SimulateKeyPress`: Use key simulation instead of direct hit.
- `SkyHookMode`: Enable SkyHook input path.
- `TimeOffset`: Trigger offset in milliseconds.
- `EnableArrowTimeAdjust` / `EnableKeyAdjust` / `AdjustStep`: Runtime hotkey adjustment controls.
- `InputMode`: Low-level input mode (Auto / NtUserInjectKeyboard / NtUserSendInput / SendInput).
- `EnableDeathKey`, `DeathKeyDelay`, `DeathKeyInput`: Death-key behavior.
- `EnableKeyFilter`, `FilterMode`, `FilteredKeys`, `FilteredAsyncKeys`: Input filtering behavior.

## Installation

1. Install **UnityModManager** and ensure ADOFAI can load UMM mods.
2. Build this project and get `BaseMacro.dll` (and required dependencies).
3. Copy output files into the corresponding `Mods/BaseMacro` directory.
4. Launch the game and enable `BaseMacro` in the UMM panel.

> The repository includes `InputSystem.dll`, which is loaded at runtime by `InputSystem.Initialize()`.

## Build Notes

This project is a .NET Framework C# project (`BaseMacro.csproj`) and references several game-managed DLLs (such as `Assembly-CSharp.dll`, `UnityEngine.dll`, and `SkyHook.Unity.dll`).

For local builds:

1. Make sure the `HintPath` entries in `BaseMacro.csproj` point to your ADOFAI installation path.
2. Build `Release` using Visual Studio or MSBuild.
3. Restore NuGet packages first if required (`packages.config` style).

## Usage Tips

- When `SimulateKeyPress` is enabled, verify `MacroKeys` first.
- If you use `SkyHookMode`, test with short levels before long runs.
- If you hit input conflicts, try:
  - Switching `InputMode`;
  - Comparing behavior with `SkyHookMode` on/off;
  - Configuring key filters to avoid duplicate input sources.

## Licenses

- Main project license: `LICENSE.txt`.
- Async input optimization license: `AsyncInputOptimize-LICENSE.txt`.
