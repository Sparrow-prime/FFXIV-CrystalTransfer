# CHANGELOG

## 1.0.1

### 现象

插件加载失败，Dalamud 报 `MissingMethodException`，`System.AggregateException: Failed to create FFXIVCrystalTransfer.Plugin (ctor invocation)`：

```
System.MissingMethodException: Method not found: 'Void Dalamud.Interface.Windowing.Window.set_SizeCondition(Dalamud.Bindings.ImGui.ImGuiCond)'.
   at FFXIVCrystalTransfer.Windows.ConfigWindow..ctor(...) in Windows\ConfigWindow.cs:line 29
   at FFXIVCrystalTransfer.Plugin..ctor(...) in Plugin.cs:line 43
```

`ConfigWindow` 构造函数无法 JIT 编译，整个插件无法启用；栈显示的第 29 行是方法入口的第一个序列点，并非真实出错语句。

### 根因

工程文件用 `<Reference>` 显式引用宿主提供的 `Dalamud.Bindings.ImGui/ImPlot/ImGuizmo`，但没有 `<Private>false</Private>`，默认 `CopyLocal=true`：

1. 这三个程序集被复制进插件输出目录，`deps.json` 把它们登记为插件自身的运行时依赖；
2. Dalamud 给 dev 插件建立的是隔离的 `ManagedLoadContext`，不把 ImGui 绑定视为共享程序集，于是从插件目录加载了**副本**；
3. 进程内因此存在两份 `Dalamud.Bindings.ImGui.ImGuiCond` 类型，调用宿主的 `Window.set_SizeCondition(ImGuiCond)` 时签名对不上 → `MissingMethodException`。

宿主 `Dalamud.dll`（15.0.3.8，与游戏实际运行的 `26-10-18-01` 同版本）本身有该成员，因此不是 API 版本超前/落后，而是类型身份被复制破坏。

### 处置

- `FFXIV-CrystalTransfer.csproj`：三个绑定引用加 `<Private>false</Private>`，并注明原因；
- 清理 `E:\Code\Work\.output\FFXIV-CrystalTransfer\` 中残留的 6 个宿主程序集（`Dalamud.Bindings.*` 的 dll + pdb）——宿主加载器在 `deps.json` 未登记时仍会按「插件目录 + 同名 dll」回退探测，只改工程文件不清目录会复发。

### 构建与产物

- 工程级 Release 构建，产物输出 `E:\Code\Work\.output\FFXIV-CrystalTransfer\`
- 输出目录现在只有 `FFXIV-CrystalTransfer.dll` / `.json` / `.deps.json`；`deps.json` 由 1706 字节降至 461 字节，不再登记宿主程序集
- 发布包 `latest.zip` 由 1 337 748 字节降至 29 402 字节（此前误把宿主 DLL 一并打包，1.0.0 的发布包同样存在该问题）
- 发布附件：`latest.zip`

### 版本号

`1.0.0.0` → `1.0.1.0`（修复，z 位递增）

### 测试

- 清理残留 + 重建：0 警告 0 错误
- 反射探针（`临时文件\ctdiag\diag.cs`，复刻 Dalamud 隔离加载上下文，触发构造函数 JIT）：
  - 修复前 `preferDefault=false`：`MissingMethodException`，与线上报错逐字一致
  - 修复后 `preferDefault=true` 与 `preferDefault=false` 均 `ctor JIT OK`，`ImGuiCond` 解析到宿主程序集
- 游戏内加载：待验收

## 版本索引

| 版本 | 日期 | 类型 |
| ---- | ---- | ---- |
| 1.0.1 | 2026-10-21 | 修复：宿主 ImGui 绑定被复制导致插件加载失败 |
| 1.0.0 | 2026-08-17 | 首个版本：水晶自动存入雇员 |

## 推送记录

（推送完成后补充）
