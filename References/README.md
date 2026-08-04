# Compile-time references

- `StArray.ModManager.dll`、`StArray.ModManager.Analyzer.dll` 和 `ImGui.NET.dll` 来自 StArray.ModManager 源码树。
- 本 Mod 依赖 `StArray.ModManager.Android` 的 `InputEvents` 广播 API（`async-input-api` 分支起提供），
  引用的 DLL 必须包含该 API，否则编译会失败。
- `StArray.ModManager.Analyzer.dll` 是编译期 Source Generator，用于生成 `[UnmanagedHook]` 的安装/卸载代码，不会打包进 Mod。

这些都是编译期引用，不要放进 AsyncInput 的 Mod 目录，手机端管理器已经提供。
