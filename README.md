# FFXIV-CrystalTransfer

FinalFantasyXIV Crystal Transfer Plugin

将背包中的水晶自动存入雇员背包的 Dalamud 插件。

## 功能

- **18 种水晶勾选**：碎晶 / 水晶 / 晶簇 × 6 属性（火冰风土雷水），每种独立勾选，名称后实时显示「背包 + 全雇员」总量
- **雇员勾选**：右侧纵列显示所有雇员真名，可独立开关是否对该雇员存入
- **自动执行**：点击「储存」按钮或输入 `/crystaltransfer auto`，自动完成：
  1. 自动交互附近的传唤铃
  2. 逐个雇员存入勾选水晶（切换雇员时保持传唤铃界面）
  3. 通过 InventoryTools IPC 实时查询数量，单种水晶达到 9999 上限的 (雇员, 水晶) 组合自动跳过
- **配置持久化**：勾选状态自动保存，无需重复设置

## 命令

| 命令 | 作用 |
| ---- | ---- |
| `/crystaltransfer` | 打开配置窗口 |
| `/crystaltransfer auto` | 直接执行水晶转移 |

## 依赖

- XIVLauncherCN（DailyRoutines 改版 Dalamud v15）
- 需要安装并启用 **InventoryTools** 插件（数量监控通过其 `AllaganTools.ItemCount` IPC 接口）

## 构建

```bash
dotnet build -c Release
```

产物位于 `bin/Release/FFXIV-CrystalTransfer.dll`。
