using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 抽签周期计算。国服抽签周期为 9 天：申请期 5 天 + 公示期 4 天。
/// 当房屋没有上报抽签信息（State=0）时，按首次发现时间推测当前阶段。
/// </summary>
public static class LotteryCycle
{
    public static readonly TimeSpan CycleLength = TimeSpan.FromDays(9);
    public static readonly TimeSpan EntryLength = TimeSpan.FromDays(5);

    public readonly record struct PhaseInfo(LotteryState State, DateTimeOffset PhaseEnd, bool Estimated);

    /// <summary>获取房屋当前所处阶段与阶段结束时间。</summary>
    public static PhaseInfo GetPhase(HouseEntry house, DateTimeOffset now)
    {
        var state = (LotteryState)house.State;
        if (state != LotteryState.Unknown && house.EndTime > 0)
        {
            return new PhaseInfo(state, DateTimeOffset.FromUnixTimeSeconds(house.EndTime), false);
        }

        // 推测：从首次发现时间起按 9 天周期滚动
        var t0 = DateTimeOffset.FromUnixTimeSeconds(house.FirstSeen);
        if (t0 > now) t0 = now;

        var elapsed = now - t0;
        var cycles = (long)(elapsed.Ticks / CycleLength.Ticks);
        var cycleStart = t0 + TimeSpan.FromTicks(cycles * CycleLength.Ticks);
        var entryEnd = cycleStart + EntryLength;
        var cycleEnd = cycleStart + CycleLength;

        return now < entryEnd
            ? new PhaseInfo(LotteryState.Available, entryEnd, true)
            : new PhaseInfo(LotteryState.ResultsPeriod, cycleEnd, true);
    }

    /// <summary>数据是否滞后</summary>
    public static bool IsStale(HouseSnapshot snapshot, int staleHours, DateTimeOffset now) =>
        now - snapshot.EffectiveSeenAt > TimeSpan.FromHours(staleHours);
}
