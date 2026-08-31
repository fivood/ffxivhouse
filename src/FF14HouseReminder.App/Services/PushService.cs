using System.Net.Http;
using System.Net.Http.Json;
using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

public interface IPushChannel
{
    string Name { get; }
    Task SendAsync(string title, string body, CancellationToken ct = default);
}

/// <summary>聚合推送：Windows Toast + 外部渠道</summary>
public class PushService : IDisposable
{
    private readonly ConfigService _config;
    private readonly ToastService _toast;
    private readonly HttpClient _http;

    public PushService(ConfigService config, ToastService toast)
    {
        _config = config;
        _toast = toast;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HousingApiClient.UserAgent);
    }

    public async Task SendAllAsync(string title, string body, string? watchKey = null)
    {
        var settings = _config.Config.Push;

        if (settings.UseToast)
            _toast.Show(title, body, watchKey);

        foreach (var channel in GetChannels(settings))
        {
            try
            {
                await channel.SendAsync(title, body);
                Logger.Info($"推送成功 [{channel.Name}] {title}");
            }
            catch (Exception ex)
            {
                Logger.Error($"推送失败 [{channel.Name}] {title}", ex);
            }
        }
    }

    /// <summary>测试推送</summary>
    public Task SendTestAsync() => SendAllAsync("FF14 抽房提醒", "这是一条测试推送，配置成功！");

    private IEnumerable<IPushChannel> GetChannels(PushSettings settings)
    {
        if (settings.TelegramEnabled
            && !string.IsNullOrWhiteSpace(settings.TelegramBotToken)
            && !string.IsNullOrWhiteSpace(settings.TelegramChatId))
        {
            yield return new TelegramChannel(_http, settings.TelegramBotToken, settings.TelegramChatId);
        }

        if (settings.WxPusherEnabled
            && !string.IsNullOrWhiteSpace(settings.WxPusherAppToken)
            && !string.IsNullOrWhiteSpace(settings.WxPusherUid))
        {
            yield return new WxPusherChannel(_http, settings.WxPusherAppToken, settings.WxPusherUid);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Telegram Bot 推送</summary>
public class TelegramChannel(HttpClient http, string botToken, string chatId) : IPushChannel
{
    public string Name => "Telegram";

    public async Task SendAsync(string title, string body, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{botToken}/sendMessage",
            new { chat_id = chatId, text = $"【{title}】\n{body}" }, ct);
        resp.EnsureSuccessStatusCode();
    }
}

/// <summary>WxPusher 推送（微信）</summary>
public class WxPusherChannel(HttpClient http, string appToken, string uid) : IPushChannel
{
    public string Name => "WxPusher";

    public async Task SendAsync(string title, string body, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            "https://wxpusher.zjiecode.com/api/send/message",
            new
            {
                appToken,
                content = body,
                summary = title,
                contentType = 1,
                uids = new[] { uid }
            }, ct);
        resp.EnsureSuccessStatusCode();
    }
}
