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
    [ObservableProperty] private bool _showHomes = true;

    public List<GameData.ServerInfo> Servers { get; } = GameData.AllServers.ToList();

    [ObservableProperty] private GameData.ServerInfo? _selectedServer;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _ingestStatusText = "";
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _showSettings;

    /// <summary>是否显示直报状态（公开版编译时不含此功能）</summary>
    public bool ShowIngestStatus => BuildFlags.HasLocalIngest;

    // 筛选（索引绑定，0 表示全部）
    [ObservableProperty] private int _areaFilterIndex;
    [ObservableProperty] private int _sizeFilterIndex;
    [ObservableProperty] private int _regionFilterIndex;
    [ObservableProperty] private int _sortIndex;

    private int AreaFilter => AreaFilterIndex - 1;                       // -1 全部
    private int SizeFilter => SizeFilterIndex - 1;                       // -1 全部
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
        _polling.StatusChanged += () => Application.Current.Dispatcher.Invoke(() =>
            StatusText = _polling.StatusText);
        _updates.UpdateChecked += () => Application.Current.Dispatcher.Invoke(() =>
        {
            HasUpdate = _updates.UpdateAvailable;
            UpdateText = _updates.UpdateAvailable ? $"新版本 v{_updates.LatestVersion} 可用" : "";
        });

        // 默认选中有关注项的服务器，否则默认拉诺西亚
        var watchServer = _config.Config.WatchList.FirstOrDefault()?.Server;
        SelectedServer = Servers.FirstOrDefault(s => s.Id == watchServer) ?? Servers.First(s => s.Id == 1042);
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
        _ = _polling.RefreshNowAsync(value.Id);
    }

    partial void OnAreaFilterIndexChanged(int value) => RefreshSalesList();
    partial void OnSizeFilterIndexChanged(int value) => RefreshSalesList();
    partial void OnRegionFilterIndexChanged(int value) => RefreshSalesList();
    partial void OnSortIndexChanged(int value) => RefreshSalesList();

    [RelayCommand]
    private void ToggleTop() => AlwaysOnTop = !AlwaysOnTop;

    /// <summary>每秒由界面定时器调用，刷新倒计时显示</summary>
    public void Tick()
    {
        Now = DateTimeOffset.Now;
        foreach (var w in WatchList) w.Refresh(Now);
        foreach (var h in SalesList) h.Refresh(Now);
        foreach (var h in Homes) h.Refresh(Now);
        IngestStatusText = BuildFlags.HasLocalIngest && App.Ingest != null ? App.Ingest.StatusText : "";
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

    [RelayCommand]
    private void AddHome()
    {
        if (HomeServer == null) return;
        if (!int.TryParse(HomeSlotText, out var slot) || slot < 1 || slot > 30) return;
        if (!int.TryParse(HomePlotText, out var plot) || plot < 1 || plot > 60) return;

        var key = new HouseKey(HomeServer.Id, HomeAreaIndex, slot - 1, plot);
        if (_config.Config.Homes.Any(h => h.Key == key)) return;

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
        HomeSlotText = HomePlotText = HomeLabelText = "";
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void RemoveHome(HomeViewModel item)
    {
        _config.Config.Homes.RemoveAll(h => h.Key == item.Item.Key);
        _config.Save();
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void EnterHome(HomeViewModel item)
    {
        item.Item.LastEnteredAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        _config.Save();
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void BackfillHome(HomeViewModel item)
    {
        if (item.BackfillDate == null) return;
        var date = item.BackfillDate.Value.Date;
        if (date > DateTime.Today) return; // 不允许未来日期
        item.Item.LastEnteredAt = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeSeconds();
        _config.Save();
        _reminders.Recompute();
        RefreshHomes();
    }

    [RelayCommand]
    private void DemolishHome(HomeViewModel item)
    {
        // 标记/取消炸房（开始旧家具 35 天保管倒计时）
        item.Item.DemolishedAt = item.Item.DemolishedAt > 0 ? 0 : DateTimeOffset.Now.ToUnixTimeSeconds();
        _config.Save();
        _reminders.Recompute();
        RefreshHomes();
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
    }

    private void RefreshSalesList()
    {
        SalesList.Clear();
        if (SelectedServer == null) return;

        var now = DateTimeOffset.Now;
        var sales = _store.GetServerSales(SelectedServer.Id)
            .Where(s => s.Data.PurchaseType == (int)PurchaseType.Lottery || s.Data.PurchaseType == (int)PurchaseType.FCFS)
            .Where(s => AreaFilter < 0 || s.Data.Area == AreaFilter)
            .Where(s => SizeFilter < 0 || s.Data.EffectiveSize == SizeFilter)
            .Where(s => RegionFilter < 0 || s.Data.RegionType == RegionFilter);

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
        _reminders.Recompute();
        RefreshAll();
    }

    [RelayCommand]
    private void RemoveWatch(WatchViewModel item)
    {
        _config.Config.WatchList.RemoveAll(w => w.Key == item.Item.Key);
        _config.Save();
        _reminders.Recompute();
        RefreshAll();
    }

    [RelayCommand]
    private void ToggleWatchMode(WatchViewModel item)
    {
        item.Item.Mode = item.Item.Mode == WatchMode.Planned ? WatchMode.Participated : WatchMode.Planned;
        item.Item.FiredReminders.Clear();
        _config.Save();
        _reminders.Recompute();
        RefreshWatchList();
    }

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

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
