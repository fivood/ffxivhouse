using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 和网页/Bot 共用同一份关注与房产：填了账号就以云端为准，本地列表只是镜像。
///
/// 为什么是「云端为准」而不是双向合并：两边都能删，union 合并会让删掉的又冒回来。
/// 桌面端的增删改一律先调 API、再整份拉回，删除行为才符合直觉。
///
/// 拉回来之后照旧走 ReminderEngine.Recompute()，所以任务计划那套兜底不受影响——
/// 程序关着的时候，Windows 任务仍按上次同步到的列表弹提醒。
/// </summary>
public class CloudSyncService : IDisposable
{
    private const string Base = "https://ff14.70015.net";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConfigService _config;
    private readonly HttpClient _http;

    public CloudSyncService(ConfigService config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HousingApiClient.UserAgent);
    }

    private GeneralSettings G => _config.Config.General;

    public bool Linked => !string.IsNullOrWhiteSpace(G.CloudUser) && !string.IsNullOrWhiteSpace(G.CloudToken);

    /// <summary>从 Bot 给的绑定链接（或直接的 u=..&amp;k=..）里解析账号</summary>
    public static (string U, string K)? ParseLink(string? text)
    {
        var m = Regex.Match(text ?? "", @"u=([A-Za-z0-9]+).*?k=([A-Za-z0-9]+)");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    // ── 云端返回的形状（只取桌面端用得上的字段）──
    private class CloudWatch
    {
        public long Server { get; set; }
        public int Area { get; set; }
        public int Slot { get; set; }
        public int Id { get; set; }
        public int Mode { get; set; }
        public string EntryNo { get; set; } = "";
    }

    private class CloudHome
    {
        public long Server { get; set; }
        public int Area { get; set; }
        public int Slot { get; set; }
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public long LastEnteredAt { get; set; }
        public long DemolishedAt { get; set; }
    }

    private class CloudState
    {
        public List<CloudWatch> Items { get; set; } = [];
        public List<CloudHome> Homes { get; set; } = [];
    }

    /// <summary>拉取云端列表覆盖本地，返回是否有变化（有变化才需要重算提醒）</summary>
    public async Task<bool> PullAsync(CancellationToken ct = default)
    {
        if (!Linked) return false;
        var url = $"{Base}/api/watch?u={Uri.EscapeDataString(G.CloudUser)}&k={Uri.EscapeDataString(G.CloudToken)}";
        var state = await _http.GetFromJsonAsync<CloudState>(url, Json, ct)
                    ?? throw new InvalidOperationException("云端返回空");

        var cfg = _config.Config;
        var changed = false;

        // 关注：按 Key 对齐，保留本地的「已触发」记录，免得同一条提醒重弹
        var oldWatches = cfg.WatchList.ToDictionary(w => w.Key);
        var watches = new List<WatchItem>();
        foreach (var w in state.Items)
        {
            var item = new WatchItem
            {
                Server = (int)w.Server, Area = w.Area, Slot = w.Slot, Id = w.Id,
                Mode = w.Mode == 1 ? WatchMode.Participated : WatchMode.Planned,
                EntryNo = w.EntryNo,
            };
            if (oldWatches.TryGetValue(item.Key, out var old))
            {
                item.FiredReminders = old.FiredReminders;
                item.DepositDeadline = old.DepositDeadline;
                item.Note = old.Note;
                if (old.Mode != item.Mode || old.EntryNo != item.EntryNo) changed = true;
            }
            else changed = true;
            watches.Add(item);
        }
        if (watches.Count != cfg.WatchList.Count) changed = true;

        var oldHomes = cfg.Homes.ToDictionary(h => h.Key);
        var homes = new List<HomeEntry>();
        foreach (var h in state.Homes)
        {
            var item = new HomeEntry
            {
                Server = (int)h.Server, Area = h.Area, Slot = h.Slot, Id = h.Id,
                Label = h.Label, LastEnteredAt = h.LastEnteredAt, DemolishedAt = h.DemolishedAt,
            };
            if (oldHomes.TryGetValue(item.Key, out var old))
            {
                if (old.LastEnteredAt != item.LastEnteredAt || old.DemolishedAt != item.DemolishedAt)
                    changed = true;
            }
            else changed = true;
            homes.Add(item);
        }
        if (homes.Count != cfg.Homes.Count) changed = true;

        cfg.WatchList = watches;
        cfg.Homes = homes;
        G.CloudSyncedAt = DateTimeOffset.Now;
        _config.Save();
        return changed;
    }

    /// <summary>首次链接时把本地已有的关注和房产推上去，别让本地攒的东西丢掉</summary>
    public async Task<int> PushLocalAsync(CancellationToken ct = default)
    {
        if (!Linked) return 0;
        var pushed = 0;
        foreach (var w in _config.Config.WatchList.ToList())
        {
            if (!await AddWatchAsync(w.Key, ct)) continue;
            pushed++;
            // 云端新建的关注一律是「计划抽」，本地是「已报名」的补一次
            if (w.Mode == WatchMode.Participated) await SetModeAsync(w.Key, w.Mode, w.EntryNo, ct);
        }
        foreach (var h in _config.Config.Homes.ToList())
        {
            if (!await AddHomeAsync(h.Key, h.Label, ct)) continue;
            pushed++;
            // 云端登记时把进屋时间记成「现在」，得把本地的真实日期补回去，否则 45 天倒计时会算错
            if (h.LastEnteredAt > 0) await EnteredAsync(h.Key, BeijingDay(h.LastEnteredAt), ct);
            if (h.DemolishedAt > 0) await DemolishedAsync(h.Key, BeijingDay(h.DemolishedAt), ct);
        }
        return pushed;
    }

    /// <summary>Unix 秒 → 北京时间的 yyyy-MM-dd（云端只收日期，按当天 00:00 起算）</summary>
    private static string BeijingDay(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");

    // ── 增删改：一律先调 API，调用方随后 PullAsync ──
    public Task<bool> AddWatchAsync(HouseKey k, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/watch", Body(k), ct);

    public Task<bool> RemoveWatchAsync(HouseKey k, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "/api/watch", Body(k), ct);

    /// <summary>明确设置报名状态和申请号码（重复调用结果一样，不是切换）</summary>
    public Task<bool> SetModeAsync(HouseKey k, WatchMode mode, string entryNo, CancellationToken ct = default)
    {
        var body = Body(k, ("entryNo", entryNo));
        body["mode"] = mode == WatchMode.Participated ? 1 : 0;   // 数字，服务端只认 0/1
        return SendAsync(HttpMethod.Post, "/api/mode", body, ct);
    }

    public Task<bool> AddHomeAsync(HouseKey k, string label, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/home", Body(k, ("label", label)), ct);

    public Task<bool> RemoveHomeAsync(HouseKey k, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "/api/home", Body(k), ct);

    /// <summary>进屋打卡；date 传 null 表示按现在算，否则 yyyy-MM-dd 补签</summary>
    public Task<bool> EnteredAsync(HouseKey k, string? date, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/entered", Body(k, ("date", date)), ct);

    /// <summary>标记 / 取消炸房（再调一次即取消）</summary>
    public Task<bool> DemolishedAsync(HouseKey k, string? date, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/demolished", Body(k, ("date", date)), ct);

    private static JsonObject Body(HouseKey k, params (string Key, string? Value)[] extra)
    {
        var o = new JsonObject
        {
            ["server"] = k.Server, ["area"] = k.Area, ["slot"] = k.Slot, ["id"] = k.Id,
        };
        foreach (var (key, value) in extra)
            if (value != null) o[key] = value;
        return o;
    }

    private async Task<bool> SendAsync(HttpMethod method, string path, JsonObject payload, CancellationToken ct)
    {
        if (!Linked) return false;
        try
        {
            payload["u"] = G.CloudUser;
            payload["k"] = G.CloudToken;
            using var req = new HttpRequestMessage(method, Base + path)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                Logger.Warn($"云端 {method} {path} 失败 {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error($"云端 {method} {path} 异常", ex);
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
