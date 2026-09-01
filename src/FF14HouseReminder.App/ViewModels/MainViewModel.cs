using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FF14HouseReminder.Models;
using FF14HouseReminder.Services;

namespace FF14HouseReminder.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly DataStore _store;
    private readonly PollingService _polling;
    private readonly ReminderEngine _reminders;
    private readonly UpdateService _updates;

    public ObservableCollection<WatchViewModel> WatchList { get; } = [];
    public ObservableCollection<HouseItemViewModel> SalesList { get; } = [];
    public ObservableCollection<HomeViewModel> Homes { get; } = [];

    // 房产登记表单
    [ObservableProperty] private GameData.ServerInfo? _homeServer;
    [ObservableProperty] private int _homeAreaIndex;
    [ObservableProperty] private string _homeSlotText = "";
    [ObservableProperty] private string _homePlotText = "";
    [ObservableProperty] private string _homeLabelText = "";
    [ObservableProperty] private string _homeHint = "";
    [ObservableProperty] private bool _showHomes = true;

    /// <summary>补签日期选择器的上限（不能选未来）</summary>
    public DateTime Today => DateTime.Today;

    public List<GameData.ServerInfo> Servers { get; } = GameData.AllServers.ToList();

    [ObservableProperty] private GameData.ServerInfo? _selectedServer;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _ingestStatusText = "";
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showMap;

    // 空列表提示
    [ObservableProperty] private bool _watchEmpty = true;
    [ObservableProperty] private bool _salesEmpty = true;
    [ObservableProperty] private string _salesEmptyText = "正在获取数据…";
    [ObservableProperty] private string _salesCountText = "";

    /// <summary>是否显示直报状态（公开版编译时不含此功能）</summary>
#if FULL_BUILD
    public bool ShowIngestStatus => true;
#else
    public bool ShowIngestStatus => false;
#endif

    // 筛选（索引绑定，0 表示全部）
    [ObservableProperty] private int _areaFilterIndex;
    [ObservableProperty] private bool _sizeS;
    [ObservableProperty] private bool _sizeM;
    [ObservableProperty] private bool _sizeL;
    [ObservableProperty] private int _regionFilterIndex;
    [ObservableProperty] private int _sortIndex;

    private int AreaFilter => AreaFilterIndex - 1;                       // -1 全部
    // 尺寸筛选改成可多选：三个都不选＝不限
    private int RegionFilter => RegionFilterIndex switch { 1 => 1, 2 => 2, _ => -1 };

    [ObservableProperty] private DateTimeOffset _now = DateTimeOffset.Now;

    public bool AlwaysOnTop
    {
        get => _config.Config.General.AlwaysOnTop;
        set
        {
            if (_config.Config.General.AlwaysOnTop == value) return;
            _config.Config.General.AlwaysOnTop = value;
            _config.Save();
            OnPropertyChanged();
        }
    }

    public MainViewModel(ConfigService config, DataStore store, PollingService polling,
        ReminderEngine reminders, UpdateService updates)
    {
        _config = config;
        _store = store;
        _polling = polling;
        _reminders = reminders;
        _updates = updates;

        _store.DataUpdated += () => Application.Current.Dispatcher.Invoke(RefreshAll);
        _store.DataUpdated += () => _ = PullCloudAsync();
        _polling.StatusChanged += () => Application.Current.Dispatcher.Invoke(() =>
            StatusText = _polling.StatusText);
        _updates.UpdateChecked += () => Application.Current.Dispatcher.Invoke(() =>
        {
            HasUpdate = _updates.UpdateAvailable;
            UpdateText = _updates.UpdateAvailable ? $"新版本 v{_updates.LatestVersion} 可用" : "";
        });

        // 恢复上次的筛选/排序（先于服务器赋值，避免用默认筛选刷一遍列表）
        var g = _config.Config.General;
        _areaFilterIndex = g.AreaFilterIndex;
        _sizeS = g.SizeS;
        _sizeM = g.SizeM;
        _sizeL = g.SizeL;
        _regionFilterIndex = g.RegionFilterIndex;
        _sortIndex = g.SortIndex;

        // 上次浏览的服务器 → 有关注项的服务器 → 默认拉诺西亚
        var watchServer = _config.Config.WatchList.FirstOrDefault()?.Server;
        SelectedServer = Servers.FirstOrDefault(s => s.Id == g.LastServer)
                         ?? Servers.FirstOrDefault(s => s.Id == watchServer)
                         ?? Servers.First(s => s.Id == 1042);
        HomeServer = SelectedServer;

        RefreshWatchList();
        RefreshHomes();
        if (_updates.UpdateAvailable)
        {
            HasUpdate = true;
            UpdateText = $"新版本 v{_updates.LatestVersion} 可用";
        }
    }

    partial void OnSelectedServerChanged(GameData.ServerInfo? value)
    {
        if (value == null) return;
        _polling.BrowsingServer = value.Id;
        RefreshSalesList();
        SaveUi();
        _ = _polling.RefreshNowAsync(value.Id);
    }

    partial void OnAreaFilterIndexChanged(int value) { RefreshSalesList(); SaveUi(); }
    partial void OnSizeSChanged(bool value) { RefreshSalesList(); SaveUi(); }
    partial void OnSizeMChanged(bool value) { RefreshSalesList(); SaveUi(); }
    partial void OnSizeLChanged(bool value) { RefreshSalesList(); SaveUi(); }
    partial void OnRegionFilterIndexChanged(int value) { RefreshSalesList(); SaveUi(); }
    partial void OnSortIndexChanged(int value) { RefreshSalesList(); SaveUi(); }

    /// <summary>记住当前浏览的服务器与筛选条件</summary>
    private void SaveUi()
    {
        var g = _config.Config.General;
        g.LastServer = SelectedServer?.Id ?? g.LastServer;
        g.AreaFilterIndex = AreaFilterIndex;
        g.SizeS = SizeS;
        g.SizeM = SizeM;
        g.SizeL = SizeL;
        g.RegionFilterIndex = RegionFilterIndex;
        g.SortIndex = SortIndex;
        _config.Save();
    }

    [RelayCommand]
    private void ToggleTop() => AlwaysOnTop = !AlwaysOnTop;

    /// <summary>每秒由界面定时器调用，刷新倒计时显示</summary>
    public void Tick()
    {
        Now = DateTimeOffset.Now;
        foreach (var w in WatchList) w.Refresh(Now);
        foreach (var h in SalesList) h.Refresh(Now);
        foreach (var h in Homes) h.Refresh(Now);
#if FULL_BUILD
        IngestStatusText = App.Ingest?.StatusText ?? "";
#endif
    }

    private void RefreshHomes()
    {
        Homes.Clear();
        // 死线最近的排最前
        foreach (var h in _config.Config.Homes.OrderBy(h => h.Deadline))
            Homes.Add(new HomeViewModel(h, Now));
    }

    [RelayCommand]
    private void ToggleHomes()
    {
        ShowHomes = !ShowHomes;
        OnPropertyChanged(nameof(HomesArrow));
    }

    public string HomesArrow => ShowHomes ? "▾" : "▸";

    // ── 云端同步（填了账号才生效）──────────────────────────────
    // 本地先改，UI 立刻响应、离线也照常用；同一个操作再推到云端。
    // 下一次拉取以云端为准，所以推失败时本地改动会被覆盖回去——这是「云端为准」的代价。
    private void SyncUp(Func<CloudSyncService, Task<bool>> op)
    {
        if (!App.Cloud.Linked) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await op(App.Cloud);
                await PullCloudAsync();
            }
            catch (Exception ex) { Logger.Error("云端同步失败", ex); }
        });
    }

    private DateTimeOffset _lastPull = DateTimeOffset.MinValue;

    /// <summary>拉一次云端列表；有变化就重算提醒并刷新界面（任务计划也跟着更新）</summary>
    public async Task PullCloudAsync(bool force = false)
    {
        if (!App.Cloud.Linked) return;
        if (!force && DateTimeOffset.Now - _lastPull < TimeSpan.FromMinutes(2)) return;
        _lastPull = DateTimeOffset.Now;
        try
        {
            var changed = await App.Cloud.PullAsync();
            if (!changed) return;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _reminders.Recompute();   // 顺带把 Windows 任务计划刷新成新列表
                RefreshWatchList();
                RefreshHomes();
            });
        }
        catch (Exception ex) { Logger.Error("拉取云端列表失败", ex); }
    }

    [RelayCommand]
    private void AddHome()
    {
        if (HomeServer == null) return;
        if (!int.TryParse(HomeSlotText, out var slot) || slot < 1 || slot > 30)
        {
            HomeHint = "区号填 1-30";
            return;
        }
        if (!int.TryParse(HomePlotText, out var plot) || plot < 1 || plot > 60)
        {
            HomeHint = "房号填 1-60";
            return;
        }

        var key = new HouseKey(HomeServer.Id, HomeAreaIndex, slot - 1, plot);
        if (_config.Config.Homes.Any(h => h.Key == key))
        {
            HomeHint = "这套房已经登记过了";
            return;
        }
        HomeHint = "";

        _config.Config.Homes.Add(new HomeEntry
        {
            Server = HomeServer.Id,
            Area = HomeAreaIndex,
            Slot = slot - 1,
            Id = plot,
            Label = string.IsNullOrWhiteSpace(HomeLabelText) ? "我的房" : HomeLabelText.Trim(),
            LastEnteredAt = DateTimeOffset.Now.ToUnixTimeSeconds() // 登记即起算
        });
        _config.Save();
        SyncUp(c => c.AddHomeAsync(key, _config.Config.Homes.First(h => h.Key == key).Label));
        HomeSlotText = HomePlotText = HomeLabelText = "";
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void RemoveHome(HomeViewModel item)
    {
        if (MessageBox.Show($"移除「{item.PositionText}」？打卡记录会一并删除。", "确认移除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _config.Config.Homes.RemoveAll(h => h.Key == item.Item.Key);
        _config.Save();
        SyncUp(c => c.RemoveHomeAsync(item.Item.Key));
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void EnterHome(HomeViewModel item)
    {
        item.Item.LastEnteredAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        _config.Save();
        SyncUp(c => c.EnteredAsync(item.Item.Key, null));
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void BackfillHome(HomeViewModel item)
    {
        if (item.BackfillDate == null)
        {
            HomeHint = "先选进房日期再补签";
            return;
        }
        var date = item.BackfillDate.Value.Date;
        if (date > DateTime.Today)
        {
            HomeHint = "进房日期不能填未来";
            return;
        }
        HomeHint = "";
        item.Item.LastEnteredAt = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeSeconds();
        _config.Save();
        SyncUp(c => c.EnteredAsync(item.Item.Key, date.ToString("yyyy-MM-dd")));
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void DemolishHome(HomeViewModel item)
    {
        // 取消炸房 / 标记炸房（选了日期就按那天起算，否则按现在）
        if (item.Item.DemolishedAt > 0)
        {
            item.Item.DemolishedAt = 0;
        }
        else
        {
            var ts = DemolishTimestamp(item);
            if (ts == null) return;
            item.Item.DemolishedAt = ts.Value;
        }
        HomeHint = "";
        _config.Save();
        SyncUp(c => c.DemolishedAsync(item.Item.Key,
            item.Item.DemolishedAt > 0 && item.BackfillDate != null
                ? item.BackfillDate.Value.Date.ToString("yyyy-MM-dd") : null));
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void SetDemolishDate(HomeViewModel item)
    {
        if (item.BackfillDate == null)
        {
            HomeHint = "先选炸房日期";
            return;
        }
        var ts = DemolishTimestamp(item);
        if (ts == null) return;
        item.Item.DemolishedAt = ts.Value;
        HomeHint = "";
        _config.Save();
        SyncUp(c => c.DemolishedAsync(item.Item.Key, item.BackfillDate!.Value.Date.ToString("yyyy-MM-dd")));
        _reminders.Recompute();
        RefreshHomes();
    }

    /// <summary>所选日期的 unix 秒；没选=现在；选了未来日期返回 null 并提示</summary>
    private long? DemolishTimestamp(HomeViewModel item)
    {
        if (item.BackfillDate == null) return DateTimeOffset.Now.ToUnixTimeSeconds();
        var date = item.BackfillDate.Value.Date;
        if (date > DateTime.Today)
        {
            HomeHint = "炸房日期不能填未来";
            return null;
        }
        return new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    private void RefreshAll()
    {
        RefreshWatchList();
        RefreshSalesList();
    }

    private void RefreshWatchList()
    {
        var now = DateTimeOffset.Now;
        // 关注列表按阶段死线从近到远排序（无数据的排最后）
        var sorted = _config.Config.WatchList
            .Select(w => (Watch: w, Snapshot: _store.Get(w.Key)))
            .OrderBy(x => x.Snapshot == null
                ? DateTimeOffset.MaxValue
                : LotteryCycle.GetPhase(x.Snapshot.Data, now).PhaseEnd)
            .ToList();

        WatchList.Clear();
        foreach (var (w, _) in sorted)
            WatchList.Add(new WatchViewModel(w, _store, _reminders, now));
        WatchEmpty = WatchList.Count == 0;
    }

    private void RefreshSalesList()
    {
        SalesList.Clear();
        SalesEmpty = true;
        SalesCountText = "";
        if (SelectedServer == null) return;

        var now = DateTimeOffset.Now;
        var all = _store.GetServerSales(SelectedServer.Id);
        var sales = all
            .Where(s => s.Data.PurchaseType == (int)PurchaseType.Lottery || s.Data.PurchaseType == (int)PurchaseType.FCFS)
            .Where(s => AreaFilter < 0 || s.Data.Area == AreaFilter)
            .Where(s => (!SizeS && !SizeM && !SizeL)
                        || (SizeS && s.Data.EffectiveSize == 0)
                        || (SizeM && s.Data.EffectiveSize == 1)
                        || (SizeL && s.Data.EffectiveSize == 2))
            // RegionType 0 = 部队/个人都可买，筛任何一种时都该出现
            .Where(s => RegionFilter < 0 || s.Data.RegionType == RegionFilter
                        || s.Data.RegionType == (int)RegionType.Both);

        IOrderedEnumerable<HouseSnapshot> ordered = SortIndex switch
        {
            1 => sales.OrderBy(s => s.Data.Price),                                    // 价格从低到高
            2 => sales.OrderByDescending(s => s.Data.Price),                          // 价格从高到低
            3 => sales.OrderBy(s => LotteryCycle.GetPhase(s.Data, now).PhaseEnd),     // 截止最近
            4 => sales.OrderByDescending(s => s.Data.EffectiveSize),                  // 尺寸 L→S
            5 => sales.OrderBy(s => s.Data.Participate),                              // 参与人数最少
            6 => sales.OrderByDescending(s => s.EffectiveSeenAt),                     // 数据最新
            _ => sales.OrderBy(s => s.Data.Area).ThenBy(s => s.Data.Slot).ThenBy(s => s.Data.ID)
        };

        var watched = _config.Config.WatchList.Select(w => w.Key).ToHashSet();
        foreach (var s in ordered)
            SalesList.Add(new HouseItemViewModel(s, watched.Contains(s.Data.Key), now));

        SalesEmpty = SalesList.Count == 0;
        SalesCountText = SalesList.Count > 0 ? $"　共 {SalesList.Count} 套" : "";
        SalesEmptyText = all.Count == 0 ? "正在获取数据…" : "没有符合条件的房屋，放宽筛选条件试试";
    }

    [RelayCommand]
    private void Refresh()
    {
        if (SelectedServer != null)
            _ = _polling.RefreshNowAsync(SelectedServer.Id);
    }

    [RelayCommand]
    private void AddWatch(HouseItemViewModel item)
    {
        var key = item.Snapshot.Data.Key;
        if (_config.Config.WatchList.Any(w => w.Key == key)) return;
        _config.Config.WatchList.Add(WatchItem.From(item.Snapshot.Data));
        _config.Save();
        SyncUp(c => c.AddWatchAsync(key));
        _reminders.Recompute();
        RefreshAll();
    }

    [RelayCommand]
    private void RemoveWatch(WatchViewModel item)
    {
        _config.Config.WatchList.RemoveAll(w => w.Key == item.Item.Key);
        _config.Save();
        SyncUp(c => c.RemoveWatchAsync(item.Item.Key));
        _reminders.Recompute();
        RefreshAll();
    }

    /// <summary>点「抽了」先就地问申请号码（可不填，纯备忘）；已报名的直接改回计划抽</summary>
    [RelayCommand]
    private void ToggleWatchMode(WatchViewModel item)
    {
        if (item.Item.Mode == WatchMode.Planned)
        {
            item.EntryNoInput = "";
            item.Asking = true;
            return;
        }
        SetWatchMode(item, WatchMode.Planned, "");
    }

    [RelayCommand]
    private void ConfirmEntered(WatchViewModel item) =>
        SetWatchMode(item, WatchMode.Participated, item.EntryNoInput.Trim());

    [RelayCommand]
    private void CancelEntered(WatchViewModel item) => item.Asking = false;

    private void SetWatchMode(WatchViewModel item, WatchMode mode, string entryNo)
    {
        item.Asking = false;
        item.Item.Mode = mode;
        item.Item.EntryNo = entryNo.Length > 16 ? entryNo[..16] : entryNo;
        item.Item.FiredReminders.Clear();
        _config.Save();
        // 报名了给自己发条回执（链接了云端的话渠道已关，这里只出 Windows 通知，不会重复）
        if (mode == WatchMode.Participated)
        {
            var body = $"{item.DisplayName} [{item.SizeName}]"
                + (item.Item.EntryNo.Length > 0 ? $"\n申请号码 #{item.Item.EntryNo}" : "");
            _ = App.Push.SendAllAsync("已记下：你报名了", body, item.Item.Key.ToString());
        }
        SyncUp(c => c.SetModeAsync(item.Item.Key, mode, item.Item.EntryNo));
        _reminders.Recompute();
        RefreshWatchList();
    }

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private void ToggleMap() => ShowMap = !ShowMap;

    [RelayCommand]
    private void OpenWebsite()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = HousingApiClient.BaseUrl,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (_updates.ReleaseUrl == null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _updates.ReleaseUrl,
            UseShellExecute = true
        });
    }
}
