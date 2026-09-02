using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 拿石之家的房屋信息校正「我的房产」。房子在哪一块以石之家为准——
/// 手填容易记错，它读的是官方数据。
///
/// 但它没有进房时间，所以打卡记录（LastEnteredAt）照旧只能靠手动或插件，
/// 这里只改房屋位置，不动倒计时。house_remain_day 只在快拆时才有值，
/// 当高危告警用。
/// </summary>
public static class RisingStonesSync
{
    /// <summary>拉一遍所有已登录的石之家账号，校正房产，返回给界面看的说明</summary>
    public static async Task<List<string>> RefreshAsync(
        ConfigService config, CloudSyncService cloud, PushService? push, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var accounts = config.Config.General.RisingStones;
        if (accounts.Count == 0) return lines;

        // 老版本重复登录会攒出同一个角色的多份凭据，顺手收拾掉（保留最后登录的那份）
        var dupes = accounts.Where(a => a.Nickname.Length > 0)
            .GroupBy(a => a.Nickname).Where(g => g.Count() > 1)
            .SelectMany(g => g.SkipLast(1)).ToList();
        if (dupes.Count > 0) accounts.RemoveAll(dupes.Contains);

        using var client = new RisingStonesClient();
        var dirty = false;

        foreach (var acc in accounts)
        {
            var ch = await client.GetCharacterAsync(acc, null, ct);
            if (ch == null)
            {
                // 读不到就等于炸房告警这条路断了，而且是静默断的，必须吼一声
                lines.Add($"{Name(acc)}：读取失败，登录已过期，去设置里重新登录");
                if (await NudgeAsync(acc, push, $"{Name(acc)} 的石之家登录已失效，读不到房屋倒计时了。"
                        + "打开桌面端 → 设置 → 石之家 → 登录石之家，重新扫一次码。"))
                    dirty = true;
                continue;
            }
            // 还没过期但快了：石之家的会话大约只给一周，提前吼比断了再吼有用
            if (acc.ExpiresAt is { } exp && exp - DateTimeOffset.Now < TimeSpan.FromDays(2))
            {
                var hours = Math.Max(0, (int)(exp - DateTimeOffset.Now).TotalHours);
                if (await NudgeAsync(acc, push, $"{Name(acc)} 的石之家登录还有约 {hours} 小时过期，"
                        + "过期后就读不到房屋倒计时了。抽空重新登录一次。"))
                    dirty = true;
            }
            acc.Nickname = ch.Name;
            acc.LastOkAt = DateTimeOffset.Now;
            dirty = true;

            if (ch.House is not { } plot || ch.ServerId == 0)
            {
                lines.Add($"{ch.Name}：石之家没给出房屋（{(ch.HouseText.Length > 0 ? ch.HouseText : "无")}）");
                continue;
            }

            var (changed, text) = await ApplyHouseAsync(config, cloud, ch, plot, ct);
            dirty |= changed;
            lines.Add(text);

            if (ch.HouseRemainDay is { } remain)
            {
                lines[^1] += $"，还剩 {remain} 天";
                await WarnAsync(config, push, ch, remain);
                dirty = true;
            }
        }

        if (dirty) config.Save();
        return lines;
    }

    /// <summary>把石之家给的地块落到「我的房产」上（认领已有的那条，或新登记一条）</summary>
    private static async Task<(bool Changed, string Text)> ApplyHouseAsync(
        ConfigService config, CloudSyncService cloud, RisingStonesCharacter ch,
        (int Area, int Slot, int Id) plot, CancellationToken ct)
    {
        var homes = config.Config.Homes;
        // 认领顺序：已挂在这个角色名下的 → 备注就是角色名的 → 地块正好对上的
        var home = homes.FirstOrDefault(h => h.RisingStonesOwner == ch.Name)
                   ?? homes.FirstOrDefault(h => h.Label == ch.Name)
                   ?? homes.FirstOrDefault(h => h.Server == ch.ServerId
                       && h.Area == plot.Area && h.Slot == plot.Slot && h.Id == plot.Id);

        if (home == null)
        {
            home = new HomeEntry
            {
                Server = ch.ServerId, Area = plot.Area, Slot = plot.Slot, Id = plot.Id,
                Label = ch.Name, RisingStonesOwner = ch.Name,
            };
            homes.Add(home);
            await cloud.AddHomeAsync(home.Key, home.Label, ct);
            return (true, $"{ch.Name}：已登记 {home.PositionText}");
        }

        var moved = home.Server != ch.ServerId || home.Area != plot.Area
                    || home.Slot != plot.Slot || home.Id != plot.Id;
        if (!moved)
        {
            var tagged = home.RisingStonesOwner != ch.Name;
            home.RisingStonesOwner = ch.Name;
            return (tagged, $"{ch.Name}：{home.PositionText}");
        }

        // 换地块等于换一套房：云端那条按主键存的，只能先删后加，再把打卡日期补回去
        var old = home.PositionText;
        await cloud.RemoveHomeAsync(home.Key, ct);
        home.Server = ch.ServerId;
        home.Area = plot.Area;
        home.Slot = plot.Slot;
        home.Id = plot.Id;
        home.RisingStonesOwner = ch.Name;
        await cloud.AddHomeAsync(home.Key, home.Label, ct);
        if (home.LastEnteredAt > 0)
            await cloud.EnteredAsync(home.Key, BeijingDay(home.LastEnteredAt), ct);
        if (home.DemolishedAt > 0)
            await cloud.DemolishedAsync(home.Key, BeijingDay(home.DemolishedAt), ct);
        return (true, $"{ch.Name}：{old} → {home.PositionText}（按石之家更正）");
    }

    /// <summary>提醒该重新登录了，一天最多一条（LastWarnedAt 持久化，重启也不会刷屏）</summary>
    private static async Task<bool> NudgeAsync(RisingStonesAccount acc, PushService? push, string body)
    {
        if (push == null) return false;
        if (acc.LastWarnedAt is { } last && DateTimeOffset.Now - last < TimeSpan.FromDays(1)) return false;
        acc.LastWarnedAt = DateTimeOffset.Now;
        await push.SendAllAsync("石之家登录要重做了", body);
        return true;
    }

    private static readonly HashSet<string> WarnedOnce = [];

    /// <summary>石之家开始报剩余天数就说明已经在拆除倒计时了，推一条；同一个天数只推一次</summary>
    private static async Task WarnAsync(ConfigService config, PushService? push, RisingStonesCharacter ch, int remain)
    {
        if (push == null) return;
        var key = $"{ch.Name}|{ch.HouseText}|{remain}";
        if (!WarnedOnce.Add(key)) return;
        await push.SendAllAsync("石之家报了拆除倒计时",
            $"{ch.Name} 的 {ch.HouseText} 还剩 {remain} 天。石之家只在快拆时才显示这个数，看到就尽快进屋。");
    }

    private static string BeijingDay(long unixSeconds) => GameTime.DayString(unixSeconds);

    private static string Name(RisingStonesAccount acc) =>
        acc.Nickname.Length > 0 ? acc.Nickname : "石之家账号";
}
