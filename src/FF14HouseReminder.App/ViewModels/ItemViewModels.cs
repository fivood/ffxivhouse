using CommunityToolkit.Mvvm.ComponentModel;
using FF14HouseReminder.Models;
using FF14HouseReminder.Services;

namespace FF14HouseReminder.ViewModels;

public static class Countdown
{
    public static string To(DateTimeOffset now, DateTimeOffset target)
    {
        var span = target - now;
        if (span <= TimeSpan.Zero) return "已到时间";
        if (span.TotalDays >= 1) return $"剩余 {(int)span.TotalDays} 天 {span.Hours} 小时";
        if (span.TotalHours >= 1) return $"剩余 {(int)span.TotalHours} 小时 {span.Minutes} 分";
        return $"剩余 {span.Minutes} 分";
    }

    /// <summary>阶段状态配色（浅色主题）</summary>
    public static string StateColor(LotteryState state) => state switch
    {
        LotteryState.Available => "#4C7A34",     // 绿：可报名
        LotteryState.ResultsPeriod => "#7B5EA7", // 紫：公示期
        LotteryState.Preparing => "#3B5BA5",     // 蓝：准备期
        _ => "#8A8A80"                           // 灰：未知
    };

    /// <summary>倒计时紧迫度配色（浅色主题）</summary>
    public static string UrgencyColor(DateTimeOffset now, DateTimeOffset target)
    {
        var span = target - now;
        if (span <= TimeSpan.FromHours(1)) return "#B03030";  // 红：1 小时内
        if (span <= TimeSpan.FromHours(24)) return "#B06030"; // 橙：24 小时内
        return "#3F3F38";
    }
}

/// <summary>关注列表项</summary>
public partial class WatchViewModel : ObservableObject
{
    public WatchItem Item { get; }
    private readonly DataStore _store;

    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _countdownText = "";
    [ObservableProperty] private string _freshnessText = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _stateColor = "#8A8A80";
    [ObservableProperty] private string _countdownColor = "#3F3F38";
    /// <summary>仅申请期允许切换报名状态（准备期/公示期防止误点）</summary>
    [ObservableProperty] private bool _canToggleMode;

    public WatchViewModel(WatchItem item, DataStore store, ReminderEngine reminders, DateTimeOffset now)
    {
        Item = item;
        _store = store;
        Refresh(now);
    }

    public string DisplayName => Item.DisplayName;
    public string ModeText => Item.Mode == WatchMode.Planned ? "计划抽" : "已报名";
    public string ToggleModeText => Item.Mode == WatchMode.Planned ? "标记已报名" : "改回计划抽";
    public string SizeName => GameData.GetSizeName(GameData.GetSize(Item.Area, Item.Id));

    public void Refresh(DateTimeOffset now)
    {
        var snapshot = _store.Get(Item.Key);
        if (snapshot == null)
        {
            StateText = "暂无数据";
            CountdownText = "等待数据…";
            FreshnessText = "";
            SourceText = "";
            return;
        }

        var house = snapshot.Data;
        var phase = LotteryCycle.GetPhase(house, now);
        StateText = house.StateText + (phase.Estimated ? "（推测）" : "");
        StateColor = Countdown.StateColor(phase.State);
        CountdownColor = Countdown.UrgencyColor(now, phase.PhaseEnd);
        CountdownText = phase.State switch
        {
            LotteryState.Available => "报名截止 " + Countdown.To(now, phase.PhaseEnd),
            LotteryState.ResultsPeriod => "公示截止 " + Countdown.To(now, phase.PhaseEnd),
            LotteryState.Preparing => "开抽 " + Countdown.To(now, phase.PhaseEnd),
            _ => ""
        };
        SourceText = snapshot.Source == HouseDataSource.Local ? "📡本地直报" : "☁网站数据";
        CanToggleMode = phase.State == LotteryState.Available;
        var age = now - snapshot.EffectiveSeenAt;
        FreshnessText = age.TotalHours >= 1
            ? $"数据更新于 {(int)age.TotalHours} 小时前"
            : $"数据更新于 {Math.Max(1, (int)age.TotalMinutes)} 分钟前";

        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ToggleModeText));
    }
}

/// <summary>在售房屋列表项</summary>
public partial class HouseItemViewModel : ObservableObject
{
    public HouseSnapshot Snapshot { get; }

    [ObservableProperty] private bool _isWatched;
    [ObservableProperty] private string _countdownText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _stateColor = "#8A8A80";
    [ObservableProperty] private string _countdownColor = "#3F3F38";

    public HouseItemViewModel(HouseSnapshot snapshot, bool isWatched, DateTimeOffset now)
    {
        Snapshot = snapshot;
        _isWatched = isWatched;
        Refresh(now);
    }

    public string PositionText => $"{Snapshot.Data.PositionText} [{Snapshot.Data.SizeName}]";
    public string PriceText => $"{Snapshot.Data.Price:N0} 金币";
    public string Badges => $"{Snapshot.Data.PurchaseTypeText} · {Snapshot.Data.RegionTypeText}" +
                            (Snapshot.Source == HouseDataSource.Local ? " · 📡" : "");
    public string WatchButtonText => IsWatched ? "已关注" : "＋关注";

    public void Refresh(DateTimeOffset now)
    {
        var house = Snapshot.Data;
        var phase = LotteryCycle.GetPhase(house, now);
        StateText = house.StateText + (phase.Estimated ? "（推测）" : "");
        StateColor = Countdown.StateColor(phase.State);
        CountdownColor = Countdown.UrgencyColor(now, phase.PhaseEnd);
        CountdownText = phase.State switch
        {
            LotteryState.Available => "报名截止 " + Countdown.To(now, phase.PhaseEnd),
            LotteryState.ResultsPeriod => "公示截止 " + Countdown.To(now, phase.PhaseEnd),
            LotteryState.Preparing => "开抽 " + Countdown.To(now, phase.PhaseEnd),
            _ => ""
        };
    }
}
