namespace FFXIVCrystalTransfer;

/// <summary>18 种水晶（碎晶 / 水晶 / 晶簇 × 6 属性），物品 ID 2~19（全球一致）。</summary>
public static class Crystals
{
    public readonly record struct CrystalInfo(uint ItemId, string EnglishName);

    public static readonly IReadOnlyList<CrystalInfo> All = new CrystalInfo[]
    {
        // 碎晶 Shard
        new(2, "Fire Shard"),
        new(3, "Ice Shard"),
        new(4, "Wind Shard"),
        new(5, "Earth Shard"),
        new(6, "Lightning Shard"),
        new(7, "Water Shard"),
        // 水晶 Crystal
        new(8, "Fire Crystal"),
        new(9, "Ice Crystal"),
        new(10, "Wind Crystal"),
        new(11, "Earth Crystal"),
        new(12, "Lightning Crystal"),
        new(13, "Water Crystal"),
        // 晶簇 Cluster
        new(14, "Fire Cluster"),
        new(15, "Ice Cluster"),
        new(16, "Wind Cluster"),
        new(17, "Earth Cluster"),
        new(18, "Lightning Cluster"),
        new(19, "Water Cluster"),
    };

    /// <summary>全部水晶物品 ID 集合（用于校验勾选配置）。</summary>
    public static readonly HashSet<uint> ItemIds = All.Select(x => x.ItemId).ToHashSet();

    /// <summary>水晶页中单种水晶的容量上限。</summary>
    public const uint CapacityPerType = 9999;
}
