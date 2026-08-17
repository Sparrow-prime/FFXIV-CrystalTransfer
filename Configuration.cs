using Dalamud.Configuration;

namespace FFXIVCrystalTransfer;

/// <summary>插件配置（Dalamud 自动持久化，无需每次重新配置）。</summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>勾选要转移的水晶物品 ID。空集合表示尚未配置（默认全部不勾选）。</summary>
    public HashSet<uint> EnabledCrystalIds { get; set; } = new();

    /// <summary>勾选要存入的雇员 RetainerId。空集合表示默认全部雇员。</summary>
    public HashSet<ulong> EnabledRetainerIds { get; set; } = new();
}
