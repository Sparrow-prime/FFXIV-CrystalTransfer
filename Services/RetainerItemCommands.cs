using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FFXIVCrystalTransfer.Services;

/// <summary>
/// AgentRetainer 的物品命令（<c>InventoryContextEvent.HandleCallback</c> 的 callbackParam）。
/// 与右键菜单选项等价（Entrust = 转移给雇员，Retrieve = 从雇员取出）。
/// </summary>
public enum RetainerItemCommand : ulong
{
    Retrieve = 0,
    Entrust = 1,
    PutUpForSale = 2,
    RetrieveQuantity = 3,
    EntrustQuantity = 4,
    HaveRetainerSellItems = 5,
}

/// <summary>
/// 通过 AgentRetainer 的 <c>InventoryContextEvent</c> 虚函数直接触发物品命令。
/// 参考 GatherBuddyReborn 的 RetainerItemCommandDispatcher。
/// </summary>
public static unsafe class RetainerItemCommands
{
    private const int InventoryContextEventOffset = 0x28;

    /// <summary>将玩家容器指定格子的物品整格存入当前雇员。</summary>
    public static bool TryEntrust(InventoryType playerContainer, uint playerSlot)
        => Dispatch(playerContainer, playerSlot, RetainerItemCommand.Entrust);

    /// <summary>将玩家容器指定格子的物品按数量存入当前雇员（会弹出数量窗口）。</summary>
    public static bool TryEntrustQuantity(InventoryType playerContainer, uint playerSlot)
        => Dispatch(playerContainer, playerSlot, RetainerItemCommand.EntrustQuantity);

    private static bool Dispatch(InventoryType container, uint slot, RetainerItemCommand command)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (agent == null || !agent->IsAgentActive())
        {
            return false;
        }

        var eventPtr = (byte*)agent + InventoryContextEventOffset;
        var vtable = *(void***)eventPtr;
        var handleCallback = (delegate* unmanaged<void*, uint, InventoryType, InventoryContextFlag, ulong, void>)vtable[0];
        handleCallback(eventPtr, slot, container, (InventoryContextFlag)0, (ulong)command);
        return true;
    }
}
