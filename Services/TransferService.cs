using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using FFXIVValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace FFXIVCrystalTransfer.Services;

/// <summary>
/// 核心转移状态机。流程（参考 GatherBuddyReborn 的 RetainerTaskExecutor）：
/// 交互传唤铃 → 选雇员 → 进入物品界面 → 逐个存入勾选水晶 → 关闭界面 → 下一雇员（传唤铃界面保持）。
/// 已满判定基于 InventoryTools IPC 实时数量（单种水晶 ≥ 9999）。
/// </summary>
public sealed unsafe class TransferService : IDisposable
{
    private enum Phase
    {
        Idle,
        InteractBell,
        WaitRetainerList,
        SelectRetainer,
        WaitRetainerMenu,
        SelectInventoryMenu,
        WaitInventory,
        DepositNextCrystal,
        WaitNumericInput,
        ConfirmNumeric,
        WaitDepositComplete,
        CloseRetainerInventory,
        WaitInventoryClosed,
        CloseRetainerList,
        Complete,
        Aborted,
    }

    private const int MaxWaitFrames = 300; // 约 5 秒超时（每帧 60fps）
    private const int MaxLogLines = 100;

    /// <summary>雇员菜单「道具管理」项的多语言文本（参考 AutoRetainerWork）。</summary>
    private static readonly string[] ItemManagementTexts =
    [
        "道具管理",
        "Entrust or withdraw items",
        "アイテムの受け渡し",
        "아이템 주고받기",
        "Gegenstände übergeben oder entnehmen",
        "Échanger des objets",
    ];

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly InventoryToolsClient _inventoryTools;
    private readonly Configuration _config;
    private readonly IChatGui _chatGui;

    private Phase _phase = Phase.Idle;
    private int _waitFrames;
    private int _settleDeposit; // 转移完成后的稳定等待帧（确认服务器同步）
    private int _exitCooldown;  // 退出操作冷却帧（避免重复发送 Callback）
    private int _retainerIndex;
    private int _crystalIndex;
    private readonly List<RetainerEntry> _retainers = new();
    private readonly List<string> _logLines = new();
    private bool _depositedAny; // 本次任务是否实际存入了水晶（用于全满反馈）

    public bool IsRunning => _phase != Phase.Idle;
    public string Status { get; private set; } = "空闲";
    public IReadOnlyList<string> LogLines => _logLines;

    private record struct RetainerEntry(byte SortedIndex, ulong RetainerId, string Name);

    public TransferService(
        IFramework framework,
        IPluginLog log,
        IGameGui gameGui,
        IObjectTable objects,
        InventoryToolsClient inventoryTools,
        Configuration config,
        IChatGui chatGui)
    {
        _framework = framework;
        _log = log;
        _gameGui = gameGui;
        _objects = objects;
        _inventoryTools = inventoryTools;
        _config = config;
        _chatGui = chatGui;
        _framework.Update += Tick;
    }

    public void Dispose() => _framework.Update -= Tick;

    /// <summary>启动一次转移任务。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            AddLog("已有转移任务在执行中");
            return;
        }

        if (_config.EnabledCrystalIds.Count == 0)
        {
            AddLog("未勾选任何水晶，请先在配置窗口勾选");
            return;
        }

        if (!_inventoryTools.IsAvailable)
        {
            AddLog("警告：InventoryTools 未启用，已满判定不可用（建议安装并启用 InventoryTools 插件）");
        }

        _retainerIndex = 0;
        _crystalIndex = 0;
        _depositedAny = false;
        SetPhase(Phase.InteractBell, "开始转移任务");
        AddLog($"开始转移：{_config.EnabledCrystalIds.Count} 种勾选水晶");
    }

    private void Tick(IFramework _)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        if (_waitFrames > 0)
        {
            _waitFrames--;
        }

        switch (_phase)
        {
            case Phase.InteractBell: TickInteractBell(); break;
            case Phase.WaitRetainerList: TickWaitRetainerList(); break;
            case Phase.SelectRetainer: TickSelectRetainer(); break;
            case Phase.WaitRetainerMenu: TickWaitRetainerMenu(); break;
            case Phase.SelectInventoryMenu: TickSelectInventoryMenu(); break;
            case Phase.WaitInventory: TickWaitInventory(); break;
            case Phase.DepositNextCrystal: TickDepositNextCrystal(); break;
            case Phase.WaitNumericInput: TickWaitNumericInput(); break;
            case Phase.ConfirmNumeric: TickConfirmNumeric(); break;
            case Phase.WaitDepositComplete: TickWaitDepositComplete(); break;
            case Phase.CloseRetainerInventory: TickCloseRetainerInventory(); break;
            case Phase.WaitInventoryClosed: TickWaitInventoryClosed(); break;
            case Phase.CloseRetainerList: TickCloseRetainerList(); break;
            case Phase.Complete: Finish("转移完成"); break;
            case Phase.Aborted: Finish("任务中止"); break;
        }
    }

    // ---------- 阶段实现 ----------

    private void TickInteractBell()
    {
        // 传唤铃界面已打开：无需重复交互（重复点击会关闭传唤铃）
        if (TryGetReadyAddon<AddonRetainerList>("RetainerList", out _))
        {
            SetPhase(Phase.WaitRetainerList, "等待雇员列表");
            return;
        }

        var bell = FindNearestBell();
        if (bell == null)
        {
            Abort("未找到传唤铃，请靠近传唤铃后重试");
            return;
        }

        var playerPos = _objects.LocalPlayer?.Position ?? Vector3.Zero;
        var distance = Vector3.Distance(bell.Position, playerPos);
        if (distance > 8f)
        {
            Abort($"距传唤铃过远（{distance:F1}m），请靠近后重试");
            return;
        }

        var ptr = (GameObject*)bell.Address;
        TargetSystem.Instance()->SetHardTarget(ptr, true);
        TargetSystem.Instance()->InteractWithObject(ptr, false);
        AddLog("已交互传唤铃");
        SetPhase(Phase.WaitRetainerList, "等待雇员列表");
    }

    private void TickWaitRetainerList()
    {
        if (TryGetReadyAddon<AddonRetainerList>("RetainerList", out _))
        {
            // 构建雇员列表（应用勾选过滤）
            _retainers.Clear();
            var manager = RetainerManager.Instance();
            if (manager != null)
            {
                var count = manager->GetRetainerCount();
                for (uint i = 0; i < count; i++)
                {
                    var retainer = manager->GetRetainerBySortedIndex(i);
                    if (retainer == null || retainer->RetainerId == 0)
                    {
                        continue;
                    }

                    if (_config.EnabledRetainerIds.Count > 0 && !_config.EnabledRetainerIds.Contains(retainer->RetainerId))
                    {
                        continue;
                    }

                    _retainers.Add(new((byte)i, retainer->RetainerId, retainer->NameString));
                }
            }

            if (_retainers.Count == 0)
            {
                Abort("没有可用的雇员（未勾选或数据未加载）");
                return;
            }

            // 玩家所有勾选水晶已无存货：无需进入任何雇员，直接完成
            if (!HasAnyPlayerCrystal())
            {
                AddLog("背包中已无勾选水晶，无需转移");
                SetPhase(Phase.CloseRetainerList, "完成");
                return;
            }

            // 注意：不重置 _retainerIndex（由 Start/退出流程推进），避免重交互后重复选择同一雇员
            AddLog($"共 {_retainers.Count} 个雇员参与转移，当前处理第 {_retainerIndex + 1} 个");
            SetPhase(Phase.SelectRetainer, "选择雇员");
            return;
        }

        if (_waitFrames <= 0)
        {
            // 诊断：输出 RetainerList 的实际状态
            var ptr = _gameGui.GetAddonByName<AtkUnitBase>("RetainerList");
            var diag = ptr == null
                ? "Addon 未找到"
                : $"可见={ptr->IsVisible}, 节点数={ptr->UldManager.NodeListCount}";
            Abort($"等待雇员列表超时（{diag}）");
        }
    }

    private void TickSelectRetainer()
    {
        // 玩家已无勾选水晶：无需再进雇员，直接完成
        if (!HasAnyPlayerCrystal())
        {
            AddLog("背包中已无勾选水晶，完成转移");
            SetPhase(Phase.CloseRetainerList, "完成");
            return;
        }

        // 传唤铃界面（RetainerList）应保持打开；若关闭则重新交互
        if (!TryGetReadyAddon<AddonRetainerList>("RetainerList", out var list))
        {
            SetPhase(Phase.InteractBell, "重新交互传唤铃");
            return;
        }

        if (_retainerIndex >= _retainers.Count)
        {
            SetPhase(Phase.CloseRetainerList, "全部雇员处理完毕");
            return;
        }

        var entry = _retainers[_retainerIndex];

        // 外层验证：该雇员无任何可存入水晶（所有勾选水晶已满/玩家无货）→ 直接跳过，不进入界面
        if (!HasDepositTargets(entry))
        {
            AddLog($"{entry.Name} 无可存入水晶（已满或玩家无货），跳过");
            _retainerIndex++;
            return;
        }

        _crystalIndex = 0;
        // 参考 OmenTools Callback 封装：FireCallback(参数个数, [2=事件码, index, 0, 0], true)
        var values = stackalloc AtkValue[4];
        values[0] = new() { Type = FFXIVValueType.Int, Int = 2 };
        values[1] = new() { Type = FFXIVValueType.Int, Int = (int)entry.SortedIndex };
        values[2] = new() { Type = FFXIVValueType.Int, Int = 0 };
        values[3] = new() { Type = FFXIVValueType.Int, Int = 0 };
        list->FireCallback(4, values, true);
        AddLog($"选择雇员：{entry.Name}");
        SetPhase(Phase.WaitRetainerMenu, "等待雇员菜单");
    }

    /// <summary>该雇员是否存在可存入目标：玩家有该水晶 且 雇员未满。</summary>
    private bool HasDepositTargets(RetainerEntry entry)
    {
        var enabled = _config.EnabledCrystalIds;
        foreach (var crystal in Crystals.All)
        {
            if (!enabled.Contains(crystal.ItemId))
            {
                continue;
            }

            if (!TryFindPlayerCrystalSlot(crystal.ItemId, out _, out var quantity) || quantity <= 0)
            {
                continue;
            }

            var count = _inventoryTools.GetRetainerCrystalCount(crystal.ItemId, entry.RetainerId);
            if (count < Crystals.CapacityPerType)
            {
                return true;
            }
        }

        return false;
    }

    private void TickWaitRetainerMenu()
    {
        // 参考 AutoRetainerWork：选择雇员后需先等 RetainerList 关闭，再等 SelectString 出现
        if (TryGetReadyAddon<AddonRetainerList>("RetainerList", out _))
        {
            return;
        }

        if (TryGetReadyAddon<AddonSelectString>("SelectString", out _))
        {
            SetPhase(Phase.SelectInventoryMenu, "选择物品菜单");
            return;
        }

        if (_waitFrames <= 0)
        {
            Abort(TryGetReadyAddon<AddonRetainerList>("RetainerList", out _)
                ? "等待雇员菜单超时（雇员列表未关闭，选择可能未生效）"
                : "等待雇员菜单超时（未弹出雇员菜单）");
        }
    }

    private void TickSelectInventoryMenu()
    {
        if (!TryGetReadyAddon<AddonSelectString>("SelectString", out var menu))
        {
            SetPhase(Phase.WaitRetainerMenu, "等待雇员菜单");
            return;
        }

        // 参考 OmenTools AddonSelectStringEvent：按文本匹配「道具管理」菜单项（文本在 AtkValues[i+7]）
        var count = menu->PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var atkValue = menu->AtkValues[i + 7];
            if (atkValue.Type == 0)
            {
                continue;
            }

            var text = atkValue.String.ToString() ?? string.Empty;
            if (ItemManagementTexts.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                var values = stackalloc AtkValue[1];
                values[0] = new() { Type = FFXIVValueType.Int, Int = i };
                menu->FireCallback(1, values, true);
                SetPhase(Phase.WaitInventory, "等待雇员界面");
                return;
            }
        }

        // 未匹配到：回退选首项
        if (count > 0)
        {
            var values = stackalloc AtkValue[1];
            values[0] = new() { Type = FFXIVValueType.Int, Int = 0 };
            menu->FireCallback(1, values, true);
            SetPhase(Phase.WaitInventory, "等待雇员界面");
            return;
        }

        SetPhase(Phase.WaitRetainerMenu, "等待雇员菜单");
    }

    private void TickWaitInventory()
    {
        // 雇员道具管理界面：雇员背包(InventoryRetainer) 或 玩家侧(InventoryBuddy) 任一就绪即可
        if (TryGetReadyAddon<AddonInventoryRetainer>("InventoryRetainer", out _) ||
            TryGetReadyAddon<AddonInventoryBuddy>("InventoryBuddy", out _) ||
            IsRetainerAgentActive())
        {
            _crystalIndex = 0;
            SetPhase(Phase.DepositNextCrystal, "开始存入水晶");
            return;
        }

        if (_waitFrames <= 0)
        {
            Abort($"等待雇员界面超时（{BuildAddonDiag("InventoryRetainer", "InventoryBuddy", "Inventory")}）");
        }
    }

    /// <summary>AgentRetainer 是否已激活（雇员界面已打开）。</summary>
    private static unsafe bool IsRetainerAgentActive()
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        return agent != null && agent->IsAgentActive();
    }

    /// <summary>诊断：输出指定 Addon 的可见/加载状态。</summary>
    private unsafe string BuildAddonDiag(params string[] names)
    {
        var parts = new List<string>();
        foreach (var name in names)
        {
            var ptr = _gameGui.GetAddonByName<AtkUnitBase>(name);
            parts.Add(ptr == null ? $"{name}=不存在" : $"{name}=可见:{ptr->IsVisible},加载:{ptr->IsFullyLoaded()}");
        }

        return string.Join("; ", parts);
    }

    private void TickDepositNextCrystal()
    {
        var enabled = _config.EnabledCrystalIds;
        while (_crystalIndex < Crystals.All.Count)
        {
            var crystal = Crystals.All[_crystalIndex];
            if (!enabled.Contains(crystal.ItemId))
            {
                _crystalIndex++;
                continue;
            }

            // 玩家水晶页中找该水晶格子
            if (!TryFindPlayerCrystalSlot(crystal.ItemId, out var slot, out var quantity) || quantity <= 0)
            {
                _crystalIndex++;
                continue;
            }

            var retainer = _retainers[_retainerIndex];

            // 已满判定：InventoryTools IPC 实时数量
            var retainerCount = _inventoryTools.GetRetainerCrystalCount(crystal.ItemId, retainer.RetainerId);
            if (retainerCount >= Crystals.CapacityPerType)
            {
                AddLog($"{retainer.Name} 的水晶 #{crystal.ItemId} 已满（{retainerCount}/{Crystals.CapacityPerType}），跳过");
                _crystalIndex++;
                continue;
            }

            // 触发存入（整格）
            if (!RetainerItemCommands.TryEntrust(InventoryType.Crystals, slot))
            {
                Abort("触发存入失败（雇员界面未激活？）");
                return;
            }

            AddLog($"存入水晶 #{crystal.ItemId}（玩家 {quantity}，雇员 {retainerCount}/{Crystals.CapacityPerType}）");
            _depositedAny = true;
            SetPhase(Phase.WaitNumericInput, "等待数量窗口");
            return;
        }

        // 当前雇员的所有勾选水晶处理完毕
        _crystalIndex = 0;
        SetPhase(Phase.CloseRetainerInventory, "关闭雇员界面");
    }

    private void TickWaitNumericInput()
    {
        if (TryGetReadyAddon<AddonInputNumeric>("InputNumeric", out _) ||
            TryGetReadyAddon<AddonRetainerItemTransferList>("RetainerItemTransferList", out _))
        {
            SetPhase(Phase.ConfirmNumeric, "确认数量");
            return;
        }

        if (_waitFrames <= 0)
        {
            // 未弹数量窗口：已直接转移或雇员已满（红字），继续下一水晶
            AddLog("未弹出数量窗口（已直接转移或已满），继续下一水晶");
            SetPhase(Phase.DepositNextCrystal, "处理下一水晶");
        }
    }

    private void TickConfirmNumeric()
    {
        // InputNumeric：参考 YesAlready 读取 AtkValues[2]/[3]（min/max），确认传最大值
        if (TryGetReadyAddon<AddonInputNumeric>("InputNumeric", out var input))
        {
            var max = input->AtkValues[3].UInt;
            var value = max != 0
                ? (int)max
                : input->NumericInput != null ? input->NumericInput->Value : 0;
            input->FireCallbackInt(value);
            AddLog($"确认存入数量：{value}");
            _settleDeposit = 12; // 转移完成后短暂稳定（0.2 秒）即继续，快速处理多种水晶
            SetPhase(Phase.WaitDepositComplete, "转移中");
            return;
        }

        // RetainerItemTransferList（道具转移数量窗口）：Callback(1) 确认
        if (TryGetReadyAddon<AddonRetainerItemTransferList>("RetainerItemTransferList", out var list))
        {
            var values = stackalloc AtkValue[1];
            values[0] = new() { Type = FFXIVValueType.Int, Int = 1 };
            list->FireCallback(1, values, true);
            AddLog("确认转移");
            _settleDeposit = 12;
            SetPhase(Phase.WaitDepositComplete, "转移中");
            return;
        }

        SetPhase(Phase.WaitDepositComplete, "等待转移完成");
    }

    private void TickWaitDepositComplete()
    {
        if (TryGetReadyAddon<AddonInputNumeric>("InputNumeric", out _) ||
            TryGetReadyAddon<AddonRetainerItemTransferList>("RetainerItemTransferList", out _) ||
            TryGetReadyAddon<AddonRetainerItemTransferProgress>("RetainerItemTransferProgress", out _))
        {
            // 窗口尚未关闭：超时则跳过，避免卡死
            if (_waitFrames <= 0)
            {
                AddLog("数量窗口超时未关闭，跳过该水晶");
                _crystalIndex++;
                SetPhase(Phase.DepositNextCrystal, "处理下一水晶");
            }

            return;
        }

        // 窗口已全部关闭：再稳定等待，确认转移在服务器端完成后再继续
        if (_settleDeposit > 0)
        {
            _settleDeposit--;
            return;
        }

        _crystalIndex++;
        SetPhase(Phase.DepositNextCrystal, "处理下一水晶");
    }

    private void TickCloseRetainerInventory()
    {
        // 操作冷却：每个退出操作后等待几帧，避免重复发送 Callback 导致菜单反复弹出
        if (_exitCooldown > 0)
        {
            _exitCooldown--;
            return;
        }

        // 参考 AutoRetainer LeaveRetainer：逐层退出直到回到雇员列表
        if (TryGetReadyAddon<AddonSelectYesno>("SelectYesno", out var yesno))
        {
            var values = stackalloc AtkValue[1];
            values[0] = new() { Type = FFXIVValueType.Int, Int = 0 };
            yesno->FireCallback(1, values, true);
            _exitCooldown = 10;
            return;
        }

        if (TryGetReadyAddon<AddonSelectString>("SelectString", out var menu))
        {
            var values = stackalloc AtkValue[1];
            values[0] = new() { Type = FFXIVValueType.Int, Int = -1 };
            menu->FireCallback(1, values, true);
            _exitCooldown = 10;
            return;
        }

        // 仍处于雇员界面（Agent 激活）：参考 AutoRetainer CloseAgentRetainer 用 Agent.Hide() 退出
        if (IsRetainerAgentActive())
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
            if (agent != null)
            {
                agent->Hide();
            }

            _exitCooldown = 10;
            return;
        }

        if (_waitFrames <= 0)
        {
            Abort("退出雇员界面超时");
            return;
        }

        // 已退出雇员界面
        AddLog("退出雇员界面完成");
        SetPhase(Phase.WaitInventoryClosed, "等待界面关闭");
    }

    private void TickWaitInventoryClosed()
    {
        // 仍有退出痕迹（确认弹窗/雇员菜单/Agent 激活）：回到退出流程继续处理
        if (TryGetReadyAddon<AddonSelectYesno>("SelectYesno", out _) ||
            TryGetReadyAddon<AddonSelectString>("SelectString", out _) ||
            IsRetainerAgentActive())
        {
            SetPhase(Phase.CloseRetainerInventory, "继续退出");
            return;
        }

        // 回到雇员列表（RetainerList 可见）即视为退出完成
        if (TryGetReadyAddon<AddonRetainerList>("RetainerList", out _))
        {
            _retainerIndex++;
            SetPhase(Phase.SelectRetainer, "下一个雇员");
            return;
        }

        if (_waitFrames <= 0)
        {
            // 退出已生效：进入下一雇员（TickSelectRetainer 自动处理重交互）
            _retainerIndex++;
            AddLog("雇员界面已退出");
            SetPhase(Phase.SelectRetainer, "选择下一个雇员");
        }
    }

    private void TickCloseRetainerList()
    {
        if (TryGetReadyAddon<AddonRetainerList>("RetainerList", out var list))
        {
            var values = stackalloc AtkValue[1];
            values[0] = new() { Type = FFXIVValueType.Int, Int = -1 };
            list->FireCallback(1, values);
            AddLog("关闭传唤铃界面");
        }

        // 聊天频道反馈（全满/无水晶/完成）
        if (_depositedAny)
        {
            Notify("FFXIV-CrystalTransfer：水晶转移完成");
        }
        else if (HasAnyPlayerCrystal())
        {
            Notify("FFXIV-CrystalTransfer：所有雇员的水晶均已满，本次未转移任何水晶");
        }
        else
        {
            Notify("FFXIV-CrystalTransfer：背包中已无勾选水晶");
        }

        SetPhase(Phase.Complete, "完成");
    }

    /// <summary>通过游戏聊天频道发送反馈消息（参考 AutoRetainer NotificationHandler：绿色带色消息）。</summary>
    private void Notify(string message)
    {
        try
        {
            _chatGui.Print(new XivChatEntry
            {
                Type = XivChatType.Notice,
                Message = new SeStringBuilder()
                    .AddUiForeground($"[FFXIV-CrystalTransfer] {message}", 527) // 527 = 绿色
                    .Build(),
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "聊天频道反馈发送失败");
        }
    }

    // ---------- 工具 ----------

    private IGameObject? FindNearestBell()
    {
        IGameObject? best = null;
        var bestDistance = float.MaxValue;
        var playerPos = _objects.LocalPlayer?.Position ?? Vector3.Zero;

        foreach (var obj in _objects)
        {
            if (obj == null || !obj.IsTargetable)
            {
                continue;
            }

            if (obj.ObjectKind is not (DalamudObjectKind.HousingEventObject or DalamudObjectKind.EventObj))
            {
                continue;
            }

            var name = obj.Name.TextValue;
            if (!name.Contains("Summoning Bell") && !name.Contains("传唤铃") && !name.Contains("呼び出し鈴"))
            {
                continue;
            }

            var distance = Vector3.Distance(obj.Position, playerPos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = obj;
            }
        }

        return best;
    }

    private bool TryFindPlayerCrystalSlot(uint itemId, out uint slot, out int quantity)
    {
        slot = 0;
        quantity = 0;
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var container = manager->GetInventoryContainer(InventoryType.Crystals);
        if (container == null)
        {
            return false;
        }

        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId != itemId)
            {
                continue;
            }

            slot = (uint)i;
            quantity = item->Quantity;
            return true;
        }

        return false;
    }

    /// <summary>玩家背包水晶页中是否存在任意勾选水晶（数量 &gt; 0）。</summary>
    private bool HasAnyPlayerCrystal()
    {
        var enabled = _config.EnabledCrystalIds;
        if (enabled.Count == 0)
        {
            return false;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var container = manager->GetInventoryContainer(InventoryType.Crystals);
        if (container == null)
        {
            return false;
        }

        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0 || item->Quantity <= 0)
            {
                continue;
            }

            if (enabled.Contains(item->ItemId))
            {
                return true;
            }
        }

        return false;
    }

    private unsafe bool TryGetReadyAddon<T>(string name, out T* addon) where T : unmanaged
    {
        addon = null;
        addon = _gameGui.GetAddonByName<T>(name);
        if (addon == null)
        {
            return false;
        }

        // 参考 OmenTools IsAddonAndNodesReady：可见且已完全加载
        var baseAddon = (AtkUnitBase*)addon;
        return baseAddon->IsVisible && baseAddon->IsFullyLoaded();
    }

    private void SetPhase(Phase phase, string status)
    {
        _phase = phase;
        _waitFrames = MaxWaitFrames;
        Status = status;
    }

    private void Abort(string reason)
    {
        AddLog($"中止：{reason}");
        SetPhase(Phase.Aborted, "已中止");
    }

    private void Finish(string reason)
    {
        AddLog(reason);
        _phase = Phase.Idle;
        _waitFrames = 0;
        Status = "空闲";
    }

    private void AddLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logLines.Add(line);
        if (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveAt(0);
        }

        _log.Information(message);
    }
}
