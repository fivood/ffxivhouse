using System.IO;
using System.Windows;
using System.Windows.Threading;
using FF14HouseReminder.Services;
using Microsoft.Web.WebView2.Core;

namespace FF14HouseReminder.Views;

/// <summary>
/// 石之家登录窗口：内嵌 WebView2 打开官网，用户自己扫码登录，登录成功后把会话 Cookie 取走。
///
/// 为什么必须用真浏览器而不是 HttpClient 直接登录：
/// 石之家的接口会看 TLS 指纹（社区脚本要靠 curl_cffi 伪装 Chrome 才通），
/// WebView2 本身就是 Chromium，指纹天然对得上；顺带扫码流程也不用自己实现。
/// 凭据只落在本机配置里，不往云端传。
/// </summary>
public partial class RisingStonesLoginWindow : Window
{
    private const string Entry = "https://ff.web.sdo.com/ff14risingstones/index.html";
    private const string CookieName = "ff14risingstones";

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly RisingStonesClient _client = new();
    private bool _checking;

    /// <summary>登录成功后拿到的凭据（用户关窗口则为 null）</summary>
    public RisingStonesAccount? Result { get; private set; }

    public RisingStonesLoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => { _poll.Stop(); _client.Dispose(); };
    }

    private async Task StartAsync()
    {
        try
        {
            // 单独的用户数据目录：和系统 Edge 的登录状态互不影响，也方便多账号各登各的
            var dir = Path.Combine(ConfigService.DataDir, "webview");
            Directory.CreateDirectory(dir);
            var env = await CoreWebView2Environment.CreateAsync(null, dir);
            await Web.EnsureCoreWebView2Async(env);

            // 点「登录」是往新窗口里开盛趣通行证的，WebView2 默认不给开新窗口，
            // 不接管的话点了没反应。把它拉回当前视图，登完会自己跳回石之家。
            Web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Web.CoreWebView2.Navigate(e.Uri);
            };
            // 顺手把当前站点显示出来，跳到通行证那边时不至于一脸懵
            Web.CoreWebView2.SourceChanged += (_, _) => ShowWhere();

            Web.CoreWebView2.Navigate(Entry);

            _poll.Tick += async (_, _) => await CheckCookieAsync();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("石之家登录窗口初始化失败", ex);
            Hint.Text = "打不开内置浏览器：本机可能缺 WebView2 运行时（Win11 一般自带），装一个再试。";
        }
    }

    private void ShowWhere()
    {
        var host = Uri.TryCreate(Web.Source?.ToString(), UriKind.Absolute, out var u) ? u.Host : "";
        Hint.Text = host.Contains("sdo.com") && !host.StartsWith("ff14risingstones")
            ? $"正在盛趣通行证（{host}）登录，登完会自动跳回石之家，这个窗口随后自己关。"
            : "在下面点「登录」，用账号或扫码都行。登录成功后本窗口自动关闭，凭据只存在本机。";
    }

    private async Task CheckCookieAsync()
    {
        if (_checking) return;
        try
        {
            // 传 null 取全部：登录会在 sdo.com 的几个子域之间跳，
            // 会话 Cookie 的域是 .sdo.com，按某一个站点去筛反而容易漏
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(null);
            var token = cookies.FirstOrDefault(c => c.Name == CookieName)?.Value;
            // 没登录时站点也会先发一个同名的会话 Cookie，光看有没有会当场误判成功。
            // 而且登录前后这个值可能不变（服务端把同一个会话标记为已登录），所以不能靠值变没变判断，
            // 只能每次都真调一次接口，能读出角色才算数（顺带验证 HttpClient 过不过得了指纹检测）
            if (string.IsNullOrWhiteSpace(token)) return;

            _checking = true;
            var candidate = new RisingStonesAccount
            {
                // 接口要求 UA 和登录时那次一致，所以在这儿一并记下来
                Cookie = $"{CookieName}={token}",
                UserAgent = Web.CoreWebView2.Settings.UserAgent,
                LinkedAt = DateTimeOffset.Now,
            };
            // 石之家的会话大约给一周，记下到期时间，好在失效前提醒重新登录
            var raw = cookies.First(c => c.Name == CookieName);
            if (raw.Expires > new DateTime(2000, 1, 1)) candidate.ExpiresAt = new DateTimeOffset(raw.Expires);

            var ch = await _client.GetCharacterAsync(candidate);
            if (ch == null || ch.Name.Length == 0)
            {
                ShowWhere();
                return;
            }

            candidate.Nickname = ch.Name;
            Result = candidate;
            _poll.Stop();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error("读取石之家登录状态失败", ex);
        }
        finally { _checking = false; }
    }
}
