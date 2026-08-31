using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using FF14HouseReminder.Services;

namespace FF14HouseReminder.Views;

/// <summary>右侧展开的设置面板（直接绑定配置对象的轻量写法）</summary>
public partial class SettingsPanel : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly ConfigService _config = App.Config;

    /// <summary>请求关闭面板（不保存）</summary>
    public event Action? RequestClose;

    public SettingsPanel()
    {
        InitializeComponent();
        DataContext = this;

        var g = _config.Config.General;
        var r = _config.Config.Reminders;
        var p = _config.Config.Push;

        // 勾选提前量时间片
        foreach (var cb in LeadChips.Children.OfType<System.Windows.Controls.CheckBox>())
            cb.IsChecked = r.LeadHours.Contains(int.Parse((string)cb.Tag));
        NotifyEntryDeadline = r.NotifyEntryDeadline;
        NotifyResultsStart = r.NotifyResultsStart;
        NotifyClaimDeadline = r.NotifyClaimDeadline;
        NotifyDepositDeadline = r.NotifyDepositDeadline;
        NotifyNextEntryStart = r.NotifyNextEntryStart;

        PollIntervalMinutes = g.PollIntervalMinutes.ToString();
        AutoStart = g.AutoStart;
        CheckUpdates = g.CheckUpdates;

        UseToast = p.UseToast;
        TelegramEnabled = p.TelegramEnabled;
        TelegramBotToken = p.TelegramBotToken;
        TelegramChatId = p.TelegramChatId;
        WxPusherEnabled = p.WxPusherEnabled;
        WxPusherAppToken = p.WxPusherAppToken;
        WxPusherUid = p.WxPusherUid;

        LocalIngestEnabled = g.LocalIngestEnabled;
        IngestAddress = $"http://127.0.0.1:{g.LocalIngestPort}/api/ingest";
        LocalIngestToken = g.LocalIngestToken;

        // 公开版不含直报功能
        if (!BuildFlags.HasLocalIngest)
            IngestSection.Visibility = Visibility.Collapsed;
    }

    public string LeadHoursText { get; set; } = ""; // 已弃用：提前量改用时间片勾选
    public bool NotifyEntryDeadline { get; set; }
    public bool NotifyResultsStart { get; set; }
    public bool NotifyClaimDeadline { get; set; }
    public bool NotifyDepositDeadline { get; set; }
    public bool NotifyNextEntryStart { get; set; }

    public string PollIntervalMinutes { get; set; } = "6";
    public bool AutoStart { get; set; }
    public bool CheckUpdates { get; set; }

    public bool UseToast { get; set; }
    public bool TelegramEnabled { get; set; }
    public string TelegramBotToken { get; set; } = "";

    private string _telegramChatId = "";
    public string TelegramChatId
    {
        get => _telegramChatId;
        set { _telegramChatId = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TelegramChatId))); }
    }

    public bool WxPusherEnabled { get; set; }
    public string WxPusherAppToken { get; set; } = "";
    public string WxPusherUid { get; set; } = "";

    public bool LocalIngestEnabled { get; set; }
    public string IngestAddress { get; set; } = "";
    public string LocalIngestToken { get; set; } = "";

    private string _pushTestResult = "";
    public string PushTestResult
    {
        get => _pushTestResult;
        set { _pushTestResult = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushTestResult))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void TestPush_Click(object sender, RoutedEventArgs e)
    {
        ApplyToConfig(); // 先套用当前填写的配置再测试
        PushTestResult = "发送中…";
        try
        {
            await App.Push.SendTestAsync();
            PushTestResult = "已发送";
        }
        catch (Exception ex)
        {
            PushTestResult = "发送失败";
            Logger.Error("测试推送失败", ex);
        }
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(LocalIngestToken);
    }

    /// <summary>通过 getUpdates 列出最近给 Bot 发过消息的会话，供用户选择自己的 Chat ID</summary>
    private async void FetchChatId_Click(object sender, RoutedEventArgs e)
    {
        var token = TelegramBotToken.Trim();
        if (string.IsNullOrEmpty(token))
        {
            PushTestResult = "请先填 Bot Token";
            return;
        }

        PushTestResult = "正在获取…";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync($"https://api.telegram.org/bot{token}/getUpdates");

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var chats = new Dictionary<long, string>();
            foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                JsonElement chat;
                if (update.TryGetProperty("message", out var m)) chat = m.GetProperty("chat");
                else if (update.TryGetProperty("my_chat_member", out var mm)) chat = mm.GetProperty("chat");
                else if (update.TryGetProperty("channel_post", out var cp)) chat = cp.GetProperty("chat");
                else continue;

                var id = chat.GetProperty("id").GetInt64();
                var name =
                    chat.TryGetProperty("username", out var u) ? "@" + u.GetString() :
                    chat.TryGetProperty("title", out var t) ? t.GetString() ?? "?" :
                    chat.TryGetProperty("first_name", out var f) ? f.GetString() ?? "?" : "?";
                chats[id] = name;
            }

            if (chats.Count == 0)
            {
                PushTestResult = "没有记录：请先在 Telegram 里给你的 Bot 发一条消息，再点获取";
                return;
            }

            var menu = new System.Windows.Controls.ContextMenu();
            foreach (var (id, name) in chats)
            {
                var item = new System.Windows.Controls.MenuItem { Header = $"{name}（{id}）" };
                var captured = id;
                item.Click += (_, _) => TelegramChatId = captured.ToString();
                menu.Items.Add(item);
            }
            menu.PlacementTarget = (System.Windows.Controls.Button)sender;
            menu.IsOpen = true;
            PushTestResult = "选择你的会话";
        }
        catch (Exception ex)
        {
            PushTestResult = "获取失败（检查 Token 是否正确）";
            Logger.Error("获取 Telegram Chat ID 失败", ex);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyToConfig();
        _config.Save();
        App.Reminders.Recompute();
        RequestClose?.Invoke();
    }

    private void ApplyToConfig()
    {
        var g = _config.Config.General;
        var r = _config.Config.Reminders;
        var p = _config.Config.Push;

        r.LeadHours = LeadChips.Children.OfType<System.Windows.Controls.CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => int.Parse((string)cb.Tag))
            .OrderByDescending(h => h).ToList();
        if (r.LeadHours.Count == 0) r.LeadHours = [24, 1];
        if (r.LeadHours.Count > 3) r.LeadHours = r.LeadHours.Take(3).ToList();
        r.NotifyEntryDeadline = NotifyEntryDeadline;
        r.NotifyResultsStart = NotifyResultsStart;
        r.NotifyClaimDeadline = NotifyClaimDeadline;
        r.NotifyDepositDeadline = NotifyDepositDeadline;
        r.NotifyNextEntryStart = NotifyNextEntryStart;

        if (int.TryParse(PollIntervalMinutes, out var minutes))
            g.PollIntervalMinutes = Math.Max(5, minutes);
        g.AutoStart = AutoStart;
        g.CheckUpdates = CheckUpdates;
        try { Services.AutoStart.Set(AutoStart); }
        catch (Exception ex) { Logger.Error("设置开机自启失败", ex); }

        p.UseToast = UseToast;
        p.TelegramEnabled = TelegramEnabled;
        p.TelegramBotToken = TelegramBotToken.Trim();
        p.TelegramChatId = TelegramChatId.Trim();
        p.WxPusherEnabled = WxPusherEnabled;
        p.WxPusherAppToken = WxPusherAppToken.Trim();
        p.WxPusherUid = WxPusherUid.Trim();

        g.LocalIngestEnabled = LocalIngestEnabled;
    }
}
