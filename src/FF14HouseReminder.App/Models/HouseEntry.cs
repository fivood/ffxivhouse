using System.Text.Json.Serialization;

namespace FF14HouseReminder.Models;

/// <summary>
/// 房屋数据，字段与售楼中心 /api/sales 返回保持一致（本地直报也复用此结构）。
/// </summary>
public class HouseEntry
{
    public int Server { get; set; }
    public int Area { get; set; }
    public int Slot { get; set; }
    public int ID { get; set; }
    public long Price { get; set; }
    public int Size { get; set; }
    public long FirstSeen { get; set; }
    public long LastSeen { get; set; }
    public int State { get; set; }
    public int Participate { get; set; }
    public int Winner { get; set; }
    public long EndTime { get; set; }
    public long UpdateTime { get; set; }
    public int PurchaseType { get; set; }
    public int RegionType { get; set; }

    [JsonIgnore]
    public HouseKey Key => new(Server, Area, Slot, ID);

    /// <summary>有效尺寸（API 返回未知时按内置尺寸表推测）</summary>
    public int EffectiveSize => Size >= 0 && Size <= 2 ? Size : GameData.GetSize(Area, ID);

    public string AreaName => GameData.GetAreaName(Area);
    public string SizeName => GameData.GetSizeName(EffectiveSize);
    public string ServerName => GameData.GetServerName(Server);

    public string PositionText => $"{AreaName} {Slot + 1}区 {ID}号";

    public string PurchaseTypeText => (PurchaseType)PurchaseType switch
    {
        Models.PurchaseType.FCFS => "先到先得",
        Models.PurchaseType.Lottery => "抽签",
        _ => "不可购买"
    };

    public string RegionTypeText => (RegionType)RegionType switch
    {
        Models.RegionType.FC => "部队",
        Models.RegionType.Personal => "个人",
        _ => "未知"
    };

    public string StateText => (LotteryState)State switch
    {
        LotteryState.Available => "现正火热预约中",
        LotteryState.ResultsPeriod => "结果已公布",
        LotteryState.Preparing => "即将开始抽签预约",
        _ => "状态未知"
    };

    /// <summary>数据最后上报时间</summary>
    public DateTimeOffset LastSeenAt => DateTimeOffset.FromUnixTimeSeconds(LastSeen);
}

public readonly record struct HouseKey(int Server, int Area, int Slot, int Id)
{
    public override string ToString() => $"{Server}:{Area}:{Slot}:{Id}";

    public static bool TryParse(string? s, out HouseKey key)
    {
        key = default;
        var parts = s?.Split(':');
        if (parts is { Length: 4 }
            && int.TryParse(parts[0], out var server)
            && int.TryParse(parts[1], out var area)
            && int.TryParse(parts[2], out var slot)
            && int.TryParse(parts[3], out var id))
        {
            key = new HouseKey(server, area, slot, id);
            return true;
        }
        return false;
    }
}

/// <summary>一份房屋数据快照（附带来源与获取时间）</summary>
public class HouseSnapshot
{
    public required HouseEntry Data { get; set; }
    public HouseDataSource Source { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>数据在游戏内最后一次被看到的时间（本地直报=抓取时刻）</summary>
    public DateTimeOffset EffectiveSeenAt =>
        Source == HouseDataSource.Local ? FetchedAt : Data.LastSeenAt;
}

/// <summary>本地直报推送体（卫月插件 → 桌面端）</summary>
public class IngestRequest
{
    public string Source { get; set; } = "dalamud";
    public List<HouseEntry> Entries { get; set; } = [];
}
