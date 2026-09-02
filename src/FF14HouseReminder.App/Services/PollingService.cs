using System.IO;
using FF14HouseReminder.Models;
using Microsoft.Win32;

namespace FF14HouseReminder.Services;

/// <summary>
/// 轮询服务：定时从售楼中心拉取数据，并触发提醒检查。
/// </summary>
public class PollingService : IDisposable
{
    private readonly ConfigService _config;
    private readonly HousingApiClient _api;
    private readonly DataStore _store;
    private readonly ReminderEngine _reminders;

    private readonly Timer _timer;
    private readonly Random _jitter = new();
    private readonly Dictionary<int, DateTimeOffset> _lastPoll = new();
    private volatile bool _polling;

    /// <summary>正在浏览的服务器（也参与轮询）</summary>
    public int? BrowsingServer { get; set; }

    public string StatusText { get; private set; } = "待启动";
    public event Action? StatusChanged;

    public PollingService(ConfigService config, HousingApiClient api, DataStore store, ReminderEngine reminders)
    {
        _config = config;
        _api = api;
        _store = store;
        _reminders = reminders;
        _store.DataUpdated += () => _reminders.Recompute();
        _timer = new Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        StatusText = "运行中";
        StatusChanged?.Invoke();
    }

    public async Task RefreshNowAsync(int serverId)
    {
        await PollServerAsync(serverId);
    }

    private async Task TickAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var servers = _config.Config.WatchList.Select(w => w.Server).ToHashSet();
            if (BrowsingServer.HasValue) servers.Add(BrowsingServer.Value);

            var interval = TimeSpan.FromMinutes(_config.Config.General.PollIntervalMinutes);
            var now = DateTimeOffset.Now;

            foreach (var server in servers)
            {
                var jitter = TimeSpan.FromSeconds(_jitter.Next(0, 60));
                if (_lastPoll.TryGetValue(server, out var last) && now - last < interval + jitter)
                    continue;
                await PollServerAsync(server);
            }

            await _reminders.FireDueAsync();
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task PollServerAsync(int serverId)
    {
        try
        {
            StatusText = $"正在更新 {GameData.GetServerName(serverId)}…";
            StatusChanged?.Invoke();

            var entries = await _api.GetSalesAsync(serverId);
            _store.MergeRemote(serverId, entries);
            _lastPoll[serverId] = DateTimeOffset.Now;

            StatusText = $"{GameData.GetServerName(serverId)} 更新于 {DateTime.Now:HH:mm}";
            Logger.Info($"已更新服务器 {serverId}（{entries.Count} 条在售）");
        }
        catch (Exception ex)
        {
            StatusText = "更新失败，稍后重试";
            Logger.Error($"轮询服务器 {serverId} 失败", ex);
        }
        StatusChanged?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>开机自启（注册表 Run 键）</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "FF14HouseReminder";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) != null;
    }

    /// <summary>注册表里那条自启命令指的还是不是当前这个 exe（更新或搬家后会变）</summary>
    public static bool PointsAtCurrentExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var cmd = key?.GetValue(AppName) as string;
        var exe = Environment.ProcessPath;
        return cmd != null && exe != null && cmd.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, "FF14HouseReminder.exe");
            // 开机启动时最小化到托盘，不弹窗糊脸
            key.SetValue(AppName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
