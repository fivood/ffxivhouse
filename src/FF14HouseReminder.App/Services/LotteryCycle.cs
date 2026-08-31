using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 抽签周期计算（与售楼中心前端算法对齐）。
/// 国服抽签周期 9 天 = 申请期 5 天 + 公示期 4 天，
/// 所有阶段在北京时间 23:00 切换（锚点：2022-08-08 23:00 +0800，当时为公示期结束）。
/// </summary>
public static class LotteryCycle
{
    /// <summary>周期锚点：2022-08-08 23:00:00 +0800（一个公示期结束/周期边界）</summary>
    private static readonly long AnchorUnix = 1659970800;
    private static readonly long CycleSec = 9 * 86400;
    private static readonly long EntrySec = 5 * 86400;   // 申请期时长
    private static readonly long ResultsSec = 4 * 86400; // 公示期时长

    public readonly record struct PhaseInfo(LotteryState State, DateTimeOffset PhaseEnd, bool Estimated);

    /// <summary>获取房屋当前所处阶段与阶段结束时间。</summary>
    public static PhaseInfo GetPhase(HouseEntry house, DateTimeOffset now)
    {
        var nowSec = now.ToUnixTimeSeconds();

        if (house.State != 0 && house.EndTime > 0)
        {
            // 已知阶段，但时间可能已过：按周期向后滚动（与网站一致）
            var state = (LotteryState)house.State;
            var end = house.EndTime;
            while (nowSec >= end)
            {
                switch (state)
                {
                    case LotteryState.Available:     // 申请期结束 → 公示期（+4天）
                        end += ResultsSec; state = LotteryState.ResultsPeriod; break;
                    case LotteryState.ResultsPeriod: // 公示期结束 → 下轮申请期（+5天）
                        end += EntrySec; state = LotteryState.Available; break;
                    case LotteryState.Preparing:     // 准备期结束 → 申请期（+5天）
                        end += EntrySec; state = LotteryState.Available; break;
                    default:
                        return new PhaseInfo(state, DateTimeOffset.FromUnixTimeSeconds(end), false);
                }
            }
            return new PhaseInfo(state, DateTimeOffset.FromUnixTimeSeconds(end), false);
        }

        // 无抽签信息（State=0）：对齐周期锚点推测（与网站一致）
        var boundary = AnchorUnix;
        var firstSeen = Math.Min(house.FirstSeen, nowSec);
        while (boundary > firstSeen + CycleSec) boundary -= CycleSec;
        while (boundary < firstSeen) boundary += CycleSec;

        if (nowSec < boundary)
        {
            // 还没到下个周期边界：准备期，等待开抽
            return new PhaseInfo(LotteryState.Preparing, DateTimeOffset.FromUnixTimeSeconds(boundary), true);
        }

        while (nowSec > boundary + CycleSec) boundary += CycleSec;
        return nowSec < boundary + EntrySec
            ? new PhaseInfo(LotteryState.Available, DateTimeOffset.FromUnixTimeSeconds(boundary + EntrySec), true)
            : new PhaseInfo(LotteryState.ResultsPeriod, DateTimeOffset.FromUnixTimeSeconds(boundary + CycleSec), true);
    }

    /// <summary>数据是否滞后</summary>
    public static bool IsStale(HouseSnapshot snapshot, int staleHours, DateTimeOffset now) =>
        now - snapshot.EffectiveSeenAt > TimeSpan.FromHours(staleHours);
}
