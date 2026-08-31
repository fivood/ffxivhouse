using System.Text.Json.Serialization;

namespace FF14HouseReminder.Models;

public class WatchItem
{
    public int Server { get; set; }
    public int Area { get; set; }
    public int Slot { get; set; }
    public int Id { get; set; }

    public WatchMode Mode { get; set; } = WatchMode.Planned;
    public string Note { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>已触发过的提醒 Key，用于去重</summary>
    public HashSet<string> FiredReminders { get; set; } = [];

    [JsonIgnore]
    public HouseKey Key => new(Server, Area, Slot, Id);

    public static WatchItem From(HouseEntry entry, WatchMode mode = WatchMode.Planned) => new()
    {
        Server = entry.Server,
        Area = entry.Area,
        Slot = entry.Slot,
        Id = entry.ID,
        Mode = mode
    };

    public string PositionText => $"{GameData.GetAreaName(Area)} {Slot + 1}区 {Id}号";

    public string DisplayName => string.IsNullOrWhiteSpace(Note)
        ? $"{GameData.GetServerName(Server)} {PositionText}"
        : $"{GameData.GetServerName(Server)} {PositionText}（{Note}）";
}

public class ReminderSettings
{
    /// <summary>截止前提醒提前量（小时），如 24 和 1 表示截止前 24 小时与 1 小时各提醒一次</summary>
    public List<int> LeadHours { get; set; } = [24, 1];

    /// <summary>申请期截止前提醒（快去报名）</summary>
    public bool NotifyEntryDeadline { get; set; } = true;

    /// <summary>开奖提醒（进入公示期）</summary>
    public bool NotifyResultsStart { get; set; } = true;

    /// <summary>公示期截止前提醒（中签确认归属死线，逾期扣 50% 申请金）</summary>
    public bool NotifyClaimDeadline { get; set; } = true;

    /// <summary>落选押金返还死线提醒（公示期结束后 90 天，逾期不返还）</summary>
    public bool NotifyDepositDeadline { get; set; } = true;

    /// <summary>下轮申请期开始提醒</summary>
    public bool NotifyNextEntryStart { get; set; } = true;

    /// <summary>数据超过该小时数未更新时，提醒文案附带滞后警告</summary>
    public int StaleHoursWarning { get; set; } = 2;
}

public class PushSettings
{
    public bool UseToast { get; set; } = true;

    public bool TelegramEnabled { get; set; }
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";

    public bool WxPusherEnabled { get; set; }
    public string WxPusherAppToken { get; set; } = "";
    public string WxPusherUid { get; set; } = "";
}

public class GeneralSettings
{
    public int PollIntervalMinutes { get; set; } = 6;
    public bool AutoStart { get; set; }
    public bool AlwaysOnTop { get; set; }

    public bool LocalIngestEnabled { get; set; } = true;
    public int LocalIngestPort { get; set; } = 17863;
    public string LocalIngestToken { get; set; } = Guid.NewGuid().ToString("N");

    public bool CheckUpdates { get; set; } = true;
    public string UpdateCheckUrl { get; set; } =
        "https://api.github.com/repos/fivood/ffxivhouse/releases/latest";

    public bool FirstRunCompleted { get; set; }
}

public class AppConfig
{
    public List<WatchItem> WatchList { get; set; } = [];
    public List<HomeEntry> Homes { get; set; } = [];
    public ReminderSettings Reminders { get; set; } = new();
    public PushSettings Push { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

/// <summary>一条已排期的提醒（持久化到 reminders.json，供 --notify 模式查找）</summary>
public class ScheduledReminder
{
    /// <summary>去重 Key：房 key | 类型 | 触发时刻</summary>
    public required string Key { get; set; }
    public required string WatchKey { get; set; }
    public ReminderType Type { get; set; }
    public DateTimeOffset FireAt { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public bool Fired { get; set; }
}
