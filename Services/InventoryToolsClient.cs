using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FFXIVCrystalTransfer.Services;

/// <summary>
/// 通过 InventoryTools（IPC 前缀 AllaganTools）查询物品数量。
/// 需要用户安装并启用 InventoryTools 插件；未安装时数量查询返回 0。
/// </summary>
public class InventoryToolsClient
{
    private readonly ICallGateSubscriber<uint, ulong, int, uint>? _itemCount;
    private readonly IPluginLog _log;
    private bool _warned;

    public InventoryToolsClient(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        try
        {
            _itemCount = pi.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCount");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to subscribe AllaganTools.ItemCount IPC");
        }
    }

    /// <summary>InventoryTools 是否可用（IPC 提供者是否注册）。</summary>
    public bool IsAvailable => _itemCount != null;

    /// <summary>查询指定归属者（玩家 ContentId / 雇员 RetainerId）在指定容器中某物品的数量。</summary>
    public uint GetCount(uint itemId, ulong ownerId, InventoryType container)
    {
        if (_itemCount == null)
        {
            if (!_warned)
            {
                _warned = true;
                _log.Warning("InventoryTools 未安装或未启用，无法通过 IPC 查询数量");
            }
            return 0;
        }

        try
        {
            return _itemCount.InvokeFunc(itemId, ownerId, (int)container);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "AllaganTools.ItemCount 调用失败");
            return 0;
        }
    }

    /// <summary>雇员水晶页中某水晶的数量。</summary>
    public uint GetRetainerCrystalCount(uint itemId, ulong retainerId)
        => GetCount(itemId, retainerId, InventoryType.RetainerCrystals);

    /// <summary>玩家水晶页中某水晶的数量。</summary>
    public uint GetPlayerCrystalCount(uint itemId, ulong playerContentId)
        => GetCount(itemId, playerContentId, InventoryType.Crystals);
}
