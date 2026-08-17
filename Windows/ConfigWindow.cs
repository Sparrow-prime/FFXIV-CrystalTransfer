using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVCrystalTransfer.Services;
using Lumina.Excel.Sheets;

namespace FFXIVCrystalTransfer.Windows;

/// <summary>
/// 配置窗口：
/// 左侧纵列 = 18 种水晶独立勾选（名字后显示 背包+全雇员 实时总量）；
/// 右侧纵列 = 雇员勾选（可开关是否对该雇员存入）。
/// </summary>
public unsafe class ConfigWindow : Window, IDisposable
{
    private const int CountRefreshFrames = 30; // 每 0.5 秒刷新一次数量
    private const float LogHeight = 150f;      // 日志区高度

    private readonly Configuration _config;
    private readonly TransferService _transfer;
    private readonly InventoryToolsClient _inventoryTools;
    private readonly IDataManager _dataManager;
    private readonly IPlayerState _playerState;
    private readonly IDalamudPluginInterface _pluginInterface;

    private readonly Dictionary<uint, string> _itemNames = new();
    private readonly Dictionary<uint, uint> _crystalTotals = new();
    private readonly List<(byte SortedIndex, ulong RetainerId, string Name)> _retainers = new();
    private bool _retainerDataReady;
    private int _refreshFrames;

    public ConfigWindow(
        Configuration config,
        TransferService transfer,
        InventoryToolsClient inventoryTools,
        IDataManager dataManager,
        IPlayerState playerState,
        IDalamudPluginInterface pluginInterface)
        : base("FFXIV-CrystalTransfer 配置###CrystalTransferConfig")
    {
        _config = config;
        _transfer = transfer;
        _inventoryTools = inventoryTools;
        _dataManager = dataManager;
        _playerState = playerState;
        _pluginInterface = pluginInterface;

        Size = new Vector2(660, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        RefreshRetainers();
        RefreshCounts();

        DrawHeader();

        ImGui.Separator();

        // 列表高度按 18 行控件（Checkbox）高度自适应，避免内容被截断
        var listHeight = Crystals.All.Count * ImGui.GetFrameHeightWithSpacing() + 14f;

        // 标签行：两列标签同高同行
        ImGui.Text("水晶（勾选参与转移；右侧数字 = 背包 + 全雇员实时总量）");
        ImGui.SameLine();
        ImGui.Text("雇员（勾选参与存入）");

        // 水晶列表（左）
        ImGui.BeginChild("###crystal_list", new Vector2(360, listHeight), true);
        foreach (var crystal in Crystals.All)
        {
            var name = GetItemName(crystal.ItemId);
            var total = _crystalTotals.GetValueOrDefault(crystal.ItemId);
            var isChecked = _config.EnabledCrystalIds.Contains(crystal.ItemId);
            if (ImGui.Checkbox($"{name}  ({total})###crystal_{crystal.ItemId}", ref isChecked))
            {
                if (isChecked)
                {
                    _config.EnabledCrystalIds.Add(crystal.ItemId);
                }
                else
                {
                    _config.EnabledCrystalIds.Remove(crystal.ItemId);
                }

                _pluginInterface.SavePluginConfig(_config);
            }
        }

        ImGui.EndChild();

        ImGui.SameLine();

        // 雇员列表（右）
        ImGui.BeginChild("###retainer_list", new Vector2(250, listHeight), true);
        if (_retainers.Count > 0)
        {
            foreach (var retainer in _retainers)
            {
                var isChecked = _config.EnabledRetainerIds.Count == 0 || _config.EnabledRetainerIds.Contains(retainer.RetainerId);
                if (ImGui.Checkbox($"{retainer.Name}###retainer_{retainer.RetainerId}", ref isChecked))
                {
                    if (isChecked)
                    {
                        _config.EnabledRetainerIds.Add(retainer.RetainerId);
                    }
                    else
                    {
                        _config.EnabledRetainerIds.Remove(retainer.RetainerId);
                    }

                    _pluginInterface.SavePluginConfig(_config);
                }
            }
        }
        else
        {
            ImGui.TextDisabled(_retainerDataReady
                ? "（暂无雇员：请确认角色已拥有雇员）"
                : "（雇员数据未就绪：请确认已登录游戏）");
        }

        ImGui.EndChild();

        ImGui.Separator();

        DrawLog();

        // 按内容自适应窗口高度
        var needed = ImGui.GetCursorPosY() + 10f;
        var currentSize = Size ?? new Vector2(660, 760);
        if (needed > currentSize.Y)
        {
            Size = new Vector2(currentSize.X, needed);
            SizeCondition = ImGuiCond.Always;
        }
    }

    private void DrawHeader()
    {
        if (_transfer.IsRunning)
        {
            ImGui.Text($"状态：{_transfer.Status}");
        }
        else
        {
            if (ImGui.Button("储存", new Vector2(120, 0)))
            {
                _transfer.Start();
            }

            ImGui.SameLine();
            ImGui.TextDisabled("或使用命令 /crystaltransfer auto");
        }
    }

    private void DrawLog()
    {
        ImGui.Text("日志");
        ImGui.BeginChild("###transfer_log", new Vector2(-1, LogHeight), true);
        foreach (var line in _transfer.LogLines)
        {
            ImGui.TextWrapped(line);
        }

        ImGui.EndChild();
    }

    private void RefreshCounts()
    {
        _refreshFrames++;
        if (_refreshFrames < CountRefreshFrames)
        {
            return;
        }

        _refreshFrames = 0;

        var playerId = _playerState.ContentId;
        _crystalTotals.Clear();
        foreach (var crystal in Crystals.All)
        {
            uint total = _inventoryTools.GetPlayerCrystalCount(crystal.ItemId, playerId);
            foreach (var retainer in _retainers)
            {
                total += _inventoryTools.GetRetainerCrystalCount(crystal.ItemId, retainer.RetainerId);
            }

            _crystalTotals[crystal.ItemId] = total;
        }
    }

    private void RefreshRetainers()
    {
        _retainers.Clear();
        _retainerDataReady = false;

        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
        {
            return;
        }

        _retainerDataReady = true;

        var count = manager->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0)
            {
                continue;
            }

            _retainers.Add(((byte)i, retainer->RetainerId, retainer->NameString));
        }
    }

    private string GetItemName(uint itemId)
    {
        if (_itemNames.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        string name;
        try
        {
            name = _dataManager.GetExcelSheet<Item>().GetRow(itemId).Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"#{itemId}";
            }
        }
        catch
        {
            name = $"#{itemId}";
        }

        _itemNames[itemId] = name;
        return name;
    }
}
