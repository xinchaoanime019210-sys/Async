# ADOFAI 异步输入 手机版

把 ADOFAI 官方 Async Input 的事件时间线移植到手机版的独立 Mod。

## 这是什么

PC 版的异步输入并不是「提前读取输入」，而是使用硬件事件时间戳，
通过 `ProcessKeyInputs(eventTick)` 驱动官方状态机在输入真实发生时更新角度。

手机版没有 SkyHook 原生库，游戏内 `AsyncInputManager.isActive` 默认是 `false`。
本 Mod 从 ModManager 的 Android 输入广播取得内核时间戳，填充官方六组 AsyncInput mask，
再由 `scrController.ProcessKeyInputs(eventTick)` 运行原版判定状态机。

目标版本缺少官方字段时，才回退到 `scrPlayer.Hit` 或 `UpdateHoldKeys` 前的角度重算。

判定链路和 hold/multipress/fail 等副作用完全走游戏原有逻辑。

## 效果预期

手机版的判定窗口本就比 PC 宽（`Pure` 0.05s、`Perfect` 0.07s、`Counted` 0.09s）。
异步输入消除的是输入消费绑定渲染帧造成的延迟和抖动：

- **低帧率或帧率不稳的设备**：提升明显
- **稳定高帧率设备**：差异有限

这是机制决定的，不是实现问题。

## 输入链路

输入时间戳由 ModManager 已安装的 `libinput.so` Hook 广播，本 Mod 不重复 Hook 原生输入层。
Android 输入线程只做值类型快照入队；游戏主线程在 `PlayerControl_Update` 前按时间批量消费，
同一帧可处理多笔事件。

官方路径使用 `scrController.PlayerControl_Update` 和 `AsyncInputManager.get_isActive`。
只有官方路径不可用时才安装旧版 `Hit`/`UpdateHoldKeys` 回退 Hook，因此回退模式可能与占用这些
方法的其他 Mod 冲突；日志会记录最终使用的入口。

## 设置

| 项 | 说明 |
|---|---|
| 启用异步输入 | 关闭后完全走游戏原版判定路径 |
| 附加偏移 (ms) | 正值把事件解释为更早发生，负值把事件解释为更晚发生 |
| 提升延迟校准精度 | 校准页使用触摸硬件时间戳 |
| 显示调试信息 | 显示官方入口、事件消费量、队列状态和回退统计 |

设置保存在 `mods/AsyncInput/settings.json`。

## 依赖与构建

需要包含 `InputEvents` 广播 API 的 StArray.ModManager（`async-input-api` 分支起）。

需要 .NET 10 SDK：

```bash
dotnet build MobilePlugin/AsyncInput.csproj -c Release
python3 package_mod.py
```

编译引用放在 `References/`（见该目录下的 README），最终 Mod 包只含 `AsyncInput.dll`。

## 安装

将 Zip 解压到手机 ModManager 的 `mods` 目录：

```text
mods/
└── AsyncInput/
    └── AsyncInput.dll
```

入口由 `IModPlugin` 自动发现，不需要 `Info.json`。

## 验证是否生效

打开「显示调试信息」后：

1. **输入入口**：应显示 `scrController.ProcessKeyInputs`。
2. **官方处理帧/消费事件**：游玩时应持续增加；低帧率下单帧可能消费多笔事件。
3. **Wall 基准**：进入关卡后应显示「已建立」。暂停、恢复、重开后会重新建立。
4. **队列溢出丢弃**：正常游玩应为 0；非 0 时会主动清空持有状态，避免卡住按键。
5. 若入口显示 `Hit` 或 `UpdateHoldKeys`，说明当前游戏版本缺少官方 API，正在使用角度回退。

## 实现说明

核心公式取自 PC 版 `AsyncInputUtils`（反汇编自 `Assembly-CSharp.dll`，releaseNumber 141）：

```
songPosition = (eventTime - dspTimeSong - calibration_i) * pitch - addoffset

angle = snappedLastAngle
      + (songPosition - player.lastHit) / crotchetAtStart * PI * speed * (isCW ? 1 : -1)
```

官方 replay 的 event tick 使用 `DateTime` 数量级的 wall tick。
帧 tick 来自 `CLOCK_REALTIME`，触摸事件来自 `CLOCK_MONOTONIC`；两者通过每帧更新的
`uptime -> wall -> uptime` 夹心锚点换算。优先调用 Android `libc.so` 的
`CLOCK_MONOTONIC`；运行时无法解析 native 符号时固定回退到同源的 `Stopwatch` 单调时钟，
避免因 P/Invoke 失败让 wall 基准永远无法建立。检测到系统时钟阶跃时清空队列并等待下一帧重建，
避免把暂停或锁屏期间的旧事件投影到新歌曲时间轴。

官方六组 mask 分别保存非帧依赖/帧依赖的持有状态，以及两套 Down/Up 边沿；触点使用稳定 slot，
不会把多指输入折叠成单个事件。处理完旧事件后会把选中行星恢复到当前帧，并同步 `cachedAngle`。
