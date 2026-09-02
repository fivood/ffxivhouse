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

    /// <summary>点了「抽了」之后，按钮就地换成申请号码输入框</summary>
    [ObservableProperty] private bool _asking;
    [ObservableProperty] private string _entryNoInput = "";

    /// <summary>能切换、且不在填号码时，才显示那个按钮</summary>
    public bool ShowModeButton => CanToggleMode && !Asking;
    partial void OnAskingChanged(bool value) => OnPropertyChanged(nameof(ShowModeButton));
    partial void OnCanToggleModeChanged(bool value) => OnPropertyChanged(nameof(ShowModeButton));

    public WatchViewModel(WatchItem item, DataStore store, ReminderEngine reminders, DateTimeOffset now)
    {
        Item = item;
        _store = store;
        Refresh(now);
    }

    public string DisplayName => Item.DisplayName;
    public string ModeText => (Item.Mode == WatchMode.Planned ? "计划抽" : "已报名")
        + (Item.EntryNo.Length > 0 ? $" #{Item.EntryNo}" : "");
    public string ToggleModeText => Item.Mode == WatchMode.Planned ? "抽了" : "✔ 改回计划抽";
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

/// <summary>我的房产列表项（炸房倒计时）</summary>
public partial class HomeViewModel : ObservableObject
{
    public HomeEntry Item { get; }

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusColor = "#8A8A80";
    /// <summary>已进屋按钮的底色：45 天倒计时每 9 天一档，没打过卡时留空走默认灰</summary>
    [ObservableProperty] private string _enterColor = "";
    [ObservableProperty] private DateTime? _backfillDate;

    public HomeViewModel(HomeEntry item, DateTimeOffset now)
    {
        Item = item;
        Refresh(now);
    }

    public string PositionText => Item.PositionText;
    public string Label => Item.Label;
    /// <summary>炸房按钮文案（再点一次取消）</summary>
    // 操作行要挤进一行，次要按钮只留图标，说明交给 ToolTip
    public string DemolishText => Item.DemolishedAt > 0 ? "↺" : "💥";
    public string DemolishTip => Item.DemolishedAt > 0
        ? "取消炸房标记"
        : "标记炸房，开始 35 天资产回收倒计时";
    /// <summary>打过卡（已进屋按钮上色）</summary>
    public bool HasEntered => Item.DemolishedAt <= 0 && Item.LastEnteredAt > 0;
    /// <summary>没炸房时才显示打卡/补签</summary>
    public bool NotDemolished => Item.DemolishedAt <= 0;
    public bool Demolished => Item.DemolishedAt > 0;

    public void Refresh(DateTimeOffset now)
    {
        // 已炸房：显示资产回收倒计时
        if (Item.DemolishedAt > 0)
        {
            var fRemain = Item.FurnitureDeadline - now;
            var fDays = (int)Math.Floor(fRemain.TotalDays);
            StatusText = fDays >= 0
                ? $"💥 已炸房，资产回收还剩 {fDays} 天（{Services.GameTime.Day(Item.FurnitureDeadline)} 到期）"
                : "💥 已炸房，资产回收已到期！";
            StatusColor = fDays <= 1 ? "#B03030" : fDays <= 5 ? "#B06030" : "#7B5EA7";
            NotifyDemolishState();
            return;
        }

        NotifyDemolishState();
        if (Item.LastEnteredAt <= 0)
        {
            StatusText = "进屋时间未知，进屋后打卡";
            StatusColor = "#8A8A80";
            EnterColor = "";
            return;
        }
        var remain = Item.Deadline - now;
        var days = (int)Math.Floor(remain.TotalDays);
        StatusText = days >= 0
            ? $"剩余 {days} 天（最后进屋 {Services.GameTime.Day(Item.Deadline.AddDays(-45))}）"
            : "已超过 45 天未进屋！";
        // 45 天切成 5 段，每段 9 天：蓝 → 青 → 绿 → 黄 → 红。
        // 按钮底色和「剩余 N 天」共用一档，别让同一个数字有两套配色
        EnterColor = days > 36 ? "#3B5BA5" : days > 27 ? "#2D8C9D" : days > 18 ? "#4C7A34"
                   : days > 9 ? "#A66A00" : "#B03030";
        StatusColor = EnterColor;
    }

    private void NotifyDemolishState()
    {
        OnPropertyChanged(nameof(DemolishText));
        OnPropertyChanged(nameof(DemolishTip));
        OnPropertyChanged(nameof(HasEntered));
        OnPropertyChanged(nameof(NotDemolished));
        OnPropertyChanged(nameof(Demolished));
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
    public string WatchButtonText => IsWatched ? "✔ 已关注" : "✚ 关注";

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
