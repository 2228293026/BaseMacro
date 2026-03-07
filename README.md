# BaseMacro

`BaseMacro` 是一个用于 **A Dance of Fire and Ice (ADOFAI)** 的 UnityModManager 模组，提供宏触发、按键模拟、异步输入与输入过滤等功能。

## 功能概览

- **自动宏触发**：根据谱面楼层时间自动触发输入。  
- **两种触发方式**：
  - 直接调用 `controller.Hit(false)`。
  - 模拟键盘按键（SendInput / SkyHook）。
- **SkyHook 异步输入模式**：支持在高频输入场景下减少抖动。
- **时间偏移与热键微调**：支持使用方向键快速调整偏移与步长。
- **死亡后按键（Death Key）**：可配置死亡时触发的键和延迟。
- **按键过滤**：支持同步/异步输入过滤配置。
- **中英文 UI 切换**：可在设置中切换显示语言。

## 主要配置项

在 Mod 的设置面板中可见（来自 `Settings.cs`）：

- `Macro`：宏总开关。
- `MacroKeys`：宏按键序列（逗号分隔，如 `J,K,L`）。
- `SimulateKeyPress`：使用按键模拟而非直接 Hit。
- `SkyHookMode`：启用 SkyHook 路径。
- `TimeOffset`：触发时间偏移（ms）。
- `EnableArrowTimeAdjust` / `EnableKeyAdjust` / `AdjustStep`：运行时快捷调整。
- `InputMode`：底层输入模式选择（Auto / NtUserInjectKeyboard / NtUserSendInput / SendInput）。
- `EnableDeathKey`、`DeathKeyDelay`、`DeathKeyInput`：死亡按键配置。
- `EnableKeyFilter`、`FilterMode`、`FilteredKeys`、`FilteredAsyncKeys`：输入过滤。

## 安装

1. 安装 **UnityModManager** 并确保游戏可加载 UMM 模组。
2. 编译本项目，得到 `BaseMacro.dll`（以及所需依赖）。
3. 将输出文件放入 UMM 对应的 `Mods/BaseMacro` 目录。
4. 启动游戏，在 UMM 面板中启用 `BaseMacro`。

> 仓库包含 `InputSystem.dll`，运行时会由 `InputSystem.Initialize()` 尝试加载。

## 构建说明

本项目为 .NET Framework C# 项目（`BaseMacro.csproj`），并引用了游戏本体目录下的若干 DLL（如 `Assembly-CSharp.dll`、`UnityEngine.dll`、`SkyHook.Unity.dll`）。

在你的本地环境中：

1. 确保 `BaseMacro.csproj` 中 `HintPath` 指向你机器上的 ADOFAI 安装目录。
2. 使用 Visual Studio / MSBuild 构建 `Release`。
3. 若缺少 NuGet 包，先执行还原（`packages.config` 模式）。

## 使用提示

- 在开启 `SimulateKeyPress` 时，建议先确认 `MacroKeys` 配置有效。
- 如果你使用 SkyHook 模式，建议先在游戏内做短曲测试以确认输入稳定性。
- 如果出现输入冲突，可尝试：
  - 调整 `InputMode`；
  - 关闭/开启 `SkyHookMode` 对比；
  - 配置按键过滤避免重复输入源。

## 许可证

- 项目许可证见 `LICENSE.txt`。
- 异步输入优化相关许可见 `AsyncInputOptimize-LICENSE.txt`。
