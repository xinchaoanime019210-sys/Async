# ADOFAI 异步输入（移动端）

为 `ADOFAI-3.3.1-2026.08.15` 移植版补齐移动输入生产端的 Mod。

## 输入链路

ModManager 提供 Android 原始触摸事件及其 `CLOCK_MONOTONIC` 时间戳。本 Mod 只负责把
这些边沿转换为游戏已有的 `SkyHookEvent`，写入游戏自己的异步队列：

```text
InputEvents
  -> OriginalAsyncProducer
  -> AsyncInputManager.keyQueue
  -> scrController.UpdateInput（APK 原有实现）
  -> ProcessKeyInputs / scrPlayer 判定（APK 原有实现）
```

队列排序、六组异步按键集合、时间点驱动和命中状态机均由 APK 原有代码完成。本 Mod
不复制消费者、不维护另一套判定状态，也不改写行星角度。移动端没有 SkyHook 原生生产者，
所以 Mod 只在游戏处于 `PlayerControl` 时临时启用已有的异步输入类型，并绕过缺失原生库的
逻辑开关；实际 `UpdateInput` 仍由 APK 执行。

触摸坐标继续交给游戏的 UI 排除逻辑检查；菜单、暂停、失去焦点和场景切换时会清空队列。
如果 APK 的异步队列、事件类型或必要 Hook 不可用，Mod 会停止加载，游戏保留原本的移动端
触摸路径。

## 效果预期

异步输入消除的是等待渲染帧采样带来的输入时间抖动，低帧率或帧率不稳定时更明显；稳定的
高帧率设备差异较小。

## 设置

| 项 | 说明 |
|---|---|
| 启用异步输入 | 关闭后回到游戏原有输入路径 |
| 附加偏移 (ms) | 正值判定更早，负值判定更晚 |
| 显示调试信息 | 显示时钟、生产者和队列统计 |

设置保存在 `mods/AsyncInput/settings.json`。

## 依赖与构建

需要包含 `InputEvents` 广播 API 的 StArray.ModManager，以及 .NET 10 SDK：

```bash
dotnet build MobilePlugin/AsyncInput.csproj -c Release
python3 package_mod.py
```

编译引用放在 `References/`，最终 Mod 包只包含 `AsyncInput.dll`。

## 验证

打开「显示调试信息」后，进入关卡并触摸屏幕：

1. 原版异步链路显示为已启用。
2. 「写入原版 queue」随触摸边沿增加。
3. 生产失败保持为零。
4. 暂停、重开或返回菜单后，队列和触点状态被清空。

`OriginalAsyncClock` 把 Android 单调时间转换为 `SkyHookEvent.GetTimeInTicks()` 使用的
`DateTime` tick，并保留 Iridium v3 的有用部分：检测大幅 DSP 时间跳变（XRUN）并用 30
个样本修正持续偏移。音频事件消费仍完全使用 APK 原有逻辑。
