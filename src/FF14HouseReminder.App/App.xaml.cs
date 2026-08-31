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
    public static LocalIngestServer? Ingest { get; private set; }
    public static UpdateService Updates { get; private set; } = null!;

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

        Logger.Info("初始化托盘");
        InitTray();

        if (BuildFlags.HasLocalIngest)
        {
            Ingest = new LocalIngestServer(Config, Store);
            _ = Ingest.StartAsync();
        }

        // 自启开关与注册表保持一致
        if (AutoStart.IsEnabled() != Config.Config.General.AutoStart)
            AutoStart.Set(Config.Config.General.AutoStart);

        Polling.Start();
        Reminders.Recompute();

        if (Config.Config.General.CheckUpdates)
            _ = Updates.CheckAsync(Config.Config.General.UpdateCheckUrl);

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

            var hasDalamud = BuildFlags.HasLocalIngest && Directory.Exists(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN"));
            var hint = hasDalamud
                ? "检测到你安装了卫月（XIVLauncherCN）！可安装配套插件，" +
                  "逛房区时自动把本服房屋数据直报到本工具（不经过网站，数据更准）。\n" +
                  "插件地址与令牌见：设置 → 本地直报。"
                : "";
            MessageBox.Show(
                "欢迎使用 FF14 抽房提醒！\n\n" +
                "使用方式：选择服务器 → 浏览在售房屋 → 点击「＋关注」即可设置提醒。\n" +
                "提醒将通过 Windows 通知发出，也可在设置中配置 Telegram / 微信推送。" +
                (hint.Length > 0 ? "\n\n" + hint : ""),
                "FF14 抽房提醒", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void InitTray()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "FF14 抽房提醒",
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
        Ingest?.Dispose();
        Api?.Dispose();
        Push?.Dispose();
        Updates?.Dispose();
        Shutdown();
    }

    public bool IsExiting => _exiting;
}
