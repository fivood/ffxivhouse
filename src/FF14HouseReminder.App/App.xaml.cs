using System.IO;
using System.Windows;
using FF14HouseReminder.Services;
using Hardcodet.Wpf.TaskbarNotification;

namespace FF14HouseReminder;

public partial class App : Application
{
    private static Mutex? _mutex;
    private TaskbarIcon? _trayIcon;

    public static ConfigService Config { get; private set; } = null!;
    public static DataStore Store { get; private set; } = null!;
    public static HousingApiClient Api { get; private set; } = null!;
    public static ToastService Toast { get; private set; } = null!;
    public static PushService Push { get; private set; } = null!;
    public static TaskSchedulerSync TaskSync { get; private set; } = null!;
    public static ReminderEngine Reminders { get; private set; } = null!;
    public static PollingService Polling { get; private set; } = null!;
#if FULL_BUILD
    public static LocalIngestServer? Ingest { get; private set; }
#endif
    public static UpdateService Updates { get; private set; } = null!;
    public static CloudSyncService Cloud { get; private set; } = null!;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        Logger.Info("===== 应用启动 =====");

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Error("未处理异常", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("任务未观察异常", args.Exception);
            args.SetObserved();
        };

        Config = new ConfigService();
        Config.Load();
        Config.Save(); // 首次运行即落盘默认配置（含直报令牌）

        // --notify 模式：由任务计划触发，弹完提醒即退出
        var notifyIndex = Array.IndexOf(e.Args, "--notify");
        if (notifyIndex >= 0 && notifyIndex + 1 < e.Args.Length)
        {
            await RunNotifyModeAsync(e.Args[notifyIndex + 1].Trim('"'));
            Shutdown();
            return;
        }

        // --apply-update 模式：由旧版本拉起来，等它退出后把自己覆盖过去
        var applyIndex = Array.IndexOf(e.Args, "--apply-update");
        if (applyIndex >= 0 && applyIndex + 2 < e.Args.Length)
        {
            ApplyUpdate(e.Args[applyIndex + 1].Trim('"'), e.Args[applyIndex + 2]);
            Shutdown();
            return;
        }

        // --refresh 模式：任务计划每天叫醒一次，只拉数据重排提醒，不开窗口
        if (e.Args.Contains("--refresh"))
        {
            await RunRefreshModeAsync();
            Shutdown();
            return;
        }

        // 首次运行且待的地方不合适（下载夹、Program Files、临时目录）就先搬家：
        // 自动更新要覆盖自己所在的文件，写不了就永远更新不了
        if (!Config.Config.General.FirstRunCompleted && !InstallLocation.IsFine())
        {
            var ans = MessageBox.Show(
                "把「抽房了吗」安装到用户目录？" + "\n\n" +
                InstallLocation.PreferredDir + "\n\n" +
                "现在的位置要么会被清理掉，要么写不进去，自动更新会失败。" + "\n" +
                "点「是」会复制过去、建一个桌面快捷方式，然后从新位置启动。",
                "抽房了吗", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes && InstallLocation.MoveAndRestart())
            {
                Shutdown();
                return;
            }
        }

        // 单实例
        _mutex = new Mutex(true, "FF14HouseReminder_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("FF14 抽房提醒已在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Logger.Info("初始化 Toast");
        Toast = new ToastService();
        Toast.Initialize();
        Toast.Activated += _ => Dispatcher.Invoke(ShowMainWindow);

        Logger.Info("初始化服务");
        Store = new DataStore();
        Api = new HousingApiClient();
        Push = new PushService(Config, Toast);
        TaskSync = new TaskSchedulerSync();
        Logger.Info("初始化提醒引擎");
        Reminders = new ReminderEngine(Config, Store, Push, TaskSync);
        Polling = new PollingService(Config, Api, Store, Reminders);
        Updates = new UpdateService();
        Cloud = new CloudSyncService(Config);

        Logger.Info("初始化托盘");
        InitTray();

#if FULL_BUILD
        Ingest = new LocalIngestServer(Config, Store);
        _ = Ingest.StartAsync();
#endif

        // 自启开关与注册表保持一致；程序换过位置的话注册表里那条路径也得刷新
        if (AutoStart.IsEnabled() != Config.Config.General.AutoStart) AutoStart.Set(Config.Config.General.AutoStart);
        else if (Config.Config.General.AutoStart && !AutoStart.PointsAtCurrentExe()) AutoStart.Set(true);

        Polling.Start();
        Reminders.Recompute();

        if (Config.Config.General.CheckUpdates)
        {
            // 老配置里存的是 GitHub API 地址，那条路拿不到安装包直链，也常被墙——迁到自家中转
            if (Config.Config.General.UpdateCheckUrl.Contains("api.github.com"))
            {
                Config.Config.General.UpdateCheckUrl = "https://ff14.70015.net/api/latest";
                Config.Save();
            }
            _ = Updates.CheckAsync(Config.Config.General.UpdateCheckUrl);
        }

        var startMinimized = e.Args.Contains("--minimized");
        if (!startMinimized)
        {
            ShowMainWindow();
        }

        // 首次运行引导
        if (!Config.Config.General.FirstRunCompleted)
        {
            Config.Config.General.FirstRunCompleted = true;
            Config.Save();

#if FULL_BUILD
            var hint = LocalIngestServer.FirstRunHint();
#else
            var hint = "";
#endif
            MessageBox.Show(
                "欢迎使用「抽房了吗」！\n\n" +
                "使用方式：选择服务器 → 浏览在售房屋 → 点击「＋关注」即可设置提醒。\n" +
                "提醒将通过 Windows 通知发出，也可在设置中配置 Telegram / 微信推送。" +
                (hint.Length > 0 ? "\n\n" + hint : ""),
                "抽房了吗", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// 把自己复制到 target 覆盖旧版本，然后启动它。
    ///
    /// 单文件 exe 运行中被系统锁着，没法自己覆盖自己，所以这一步只能由新版本
    /// 在旧版本退出之后来做——本进程就是从更新包里解出来的新版本。
    /// </summary>
    private static void ApplyUpdate(string target, string oldPid)
    {
        try
        {
            if (int.TryParse(oldPid, out var pid))
            {
                try
                {
                    var old = System.Diagnostics.Process.GetProcessById(pid);
                    if (!old.WaitForExit(30_000))
                    {
                        Logger.Error("更新失败：旧版本 30 秒没退出", new TimeoutException());
                        return;
                    }
                }
                catch (ArgumentException) { /* 已经退干净了 */ }
            }

            // 文件句柄有时会晚一点才放开，重试几次
            for (var i = 0; ; i++)
            {
                try { File.Copy(Environment.ProcessPath!, target, true); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                WorkingDirectory = Path.GetDirectoryName(target),
                UseShellExecute = false,
            });
            Logger.Info($"更新完成，已替换 {target}");
        }
        catch (Exception ex)
        {
            Logger.Error("应用更新失败", ex);
            MessageBox.Show(
                $"自动更新没能完成：{ex.Message}\n\n可以到发布页手动下载覆盖。",
                "抽房了吗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>任务计划调用的无窗口提醒模式</summary>
    private static async Task RunNotifyModeAsync(string reminderKey)
    {
        try
        {
            var reminders = Config.LoadReminders();
            var reminder = reminders.FirstOrDefault(r => r.Key == reminderKey && !r.Fired);
            if (reminder == null) return;

            var toast = new ToastService();
            toast.Initialize();
            var push = new PushService(Config, toast);
            await push.SendAllAsync(reminder.Title, reminder.Body, reminder.WatchKey);
            push.Dispose();

            reminder.Fired = true;
            Config.SaveReminders(reminders);
            Logger.Info($"[notify 模式] 已触发提醒 {reminderKey}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[notify 模式] 触发失败 {reminderKey}", ex);
        }
    }

    /// <summary>
    /// 任务计划每天调一次的无窗口刷新：拉数据 → 重排提醒 → 更新任务计划。
    /// Recompute 只覆盖到之后两个阶段（约两周），程序长期不开时靠这个把队列续上。
    /// </summary>
    private static async Task RunRefreshModeAsync()
    {
        try
        {
            // 程序正开着的话它自己在轮询，这里让开，免得两边各弹一次
            using var mutex = new Mutex(true, "FF14HouseReminder_SingleInstance", out var isNew);
            if (!isNew) return;

            var store = new DataStore();
            using var api = new HousingApiClient();
            var toast = new ToastService();
            toast.Initialize();
            using var push = new PushService(Config, toast);
            var reminders = new ReminderEngine(Config, store, push, new TaskSchedulerSync());

            Cloud = new CloudSyncService(Config);
            if (Cloud.Linked)
            {
                try { await Cloud.PullAsync(); }
                catch (Exception ex) { Logger.Error("[refresh 模式] 拉取云端失败", ex); }
            }

            var fetched = true;
            foreach (var server in Config.Config.WatchList.Select(w => w.Server).Distinct())
            {
                try { store.MergeRemote(server, await api.GetSalesAsync(server)); }
                catch (Exception ex)
                {
                    fetched = false;
                    Logger.Error($"[refresh 模式] 拉取服务器 {server} 失败", ex);
                }
            }
            // 数据缺一块就整轮不动：现成的任务计划比按残缺数据重排出来的强
            if (!fetched)
            {
                Logger.Warn("[refresh 模式] 有服务器拉取失败，保持原有提醒计划");
                return;
            }

            reminders.Recompute();
            await reminders.FireDueAsync();
            Logger.Info("[refresh 模式] 已重排提醒计划");
        }
        catch (Exception ex)
        {
            Logger.Error("[refresh 模式] 失败", ex);
        }
    }

    private void InitTray()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "抽房了吗",
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Resources/house.ico"))
        };

        var menu = new System.Windows.Controls.ContextMenu();
        var openItem = new System.Windows.Controls.MenuItem { Header = "打开主界面" };
        openItem.Click += (_, _) => ShowMainWindow();
        var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => RealExit();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (MainWindow == null)
            MainWindow = new MainWindow();
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private bool _exiting;

    public void RealExit()
    {
        _exiting = true;
        try { Config.Save(); } catch { }
        _trayIcon?.Dispose();
        Polling?.Dispose();
#if FULL_BUILD
        Ingest?.Dispose();
#endif
        Api?.Dispose();
        Push?.Dispose();
        Updates?.Dispose();
        Shutdown();
    }

    public bool IsExiting => _exiting;
}
