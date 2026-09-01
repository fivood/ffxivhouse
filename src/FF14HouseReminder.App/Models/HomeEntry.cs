using System.Text.Json.Serialization;

namespace FF14HouseReminder.Models;

/// <summary>我的房产（炸房提醒）：手动登记 + 进房打卡/补签</summary>
public class HomeEntry
{
    public int Server { get; set; }
    public int Area { get; set; }
    public int Slot { get; set; }
    public int Id { get; set; }

    /// <summary>备注（一般是角色名，多账号区分用）</summary>
    public string Label { get; set; } = "我的房";

    /// <summary>最后一次进房时间（unix 秒），0=未知</summary>
    public long LastEnteredAt { get; set; }

    /// <summary>炸房（被拆除）时间（unix 秒），0=未炸房。拆除后 35 天内可回收资产</summary>
    public long DemolishedAt { get; set; }

    [JsonIgnore]
    public HouseKey Key => new(Server, Area, Slot, Id);

    public string PositionText =>
        $"{GameData.GetServerName(Server)} {GameData.GetAreaName(Area)} {Slot + 1}区 {Id}号";

    /// <summary>45 天拆除死线</summary>
    public DateTimeOffset Deadline =>
        DateTimeOffset.FromUnixTimeSeconds(LastEnteredAt).AddDays(45);

    /// <summary>资产回收死线（拆除后 35 天）</summary>
    public DateTimeOffset FurnitureDeadline =>
        DateTimeOffset.FromUnixTimeSeconds(DemolishedAt).AddDays(35);
}
