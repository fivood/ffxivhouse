namespace FF14HouseReminder.Services;

/// <summary>
/// 游戏按日本时间数天数，00:00 跨一天。
///
/// 所以「第 N 天」的死线是「那件事发生当天的 JST 00:00」再加 N 天，
/// 而不是从当时那一刻整整加 N×24 小时——后者会比真实死线晚最多一整天，
/// 等提醒到了房子已经拆了。
/// </summary>
public static class GameTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(9);

    /// <summary>这个时刻所在游戏日的 00:00</summary>
    public static DateTimeOffset DayStart(long unixSeconds)
    {
        var jst = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(Offset);
        return new DateTimeOffset(jst.Date, Offset);
    }

    /// <summary>某个日历日（用户选的日期）对应的游戏日 00:00</summary>
    public static DateTimeOffset DayStartOf(DateTime date) => new(date.Date, Offset);

    /// <summary>发生在 unixSeconds 的事，N 天后的死线</summary>
    public static DateTimeOffset DayDeadline(long unixSeconds, int days) =>
        DayStart(unixSeconds).AddDays(days);

    /// <summary>按游戏日显示，别用本机时区——在国内会差一天，看着像 bug</summary>
    public static string Day(DateTimeOffset t) => t.ToOffset(Offset).ToString("MM-dd");
    public static string DayTime(DateTimeOffset t) => t.ToOffset(Offset).ToString("MM-dd HH:mm");

    /// <summary>Unix 秒 → 游戏日的 yyyy-MM-dd</summary>
    public static string DayString(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(Offset).ToString("yyyy-MM-dd");
}
