using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 石之家（官方社区）个人数据：能读到角色名下是哪套房，以及快拆时的剩余天数。
/// 注意它并没有「进房时间」，所以替代不了打卡，只能当兜底告警用。
///
/// 没有开放接口也没有 token：认证就是网页登录后的会话 Cookie，
/// 而且服务端会校验 UA 与登录时一致，所以两样都要跟着账号一起存。
/// 站点还看 TLS 指纹，HttpClient 未必通得过——通不过就得改走 WebView2 里发请求。
/// </summary>
public class RisingStonesClient : IDisposable
{
    private const string Api = "https://apiff14risingstones.web.sdo.com";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// 拉个人信息原样返回。带 uuid 就是看别人的主页（石之家个人页地址栏里那串），
    /// 看得到多少取决于对方的隐私开关。
    /// </summary>
    public async Task<JsonNode?> GetUserInfoAsync(RisingStonesAccount account, string? uuid = null, CancellationToken ct = default)
    {
        var url = $"{Api}/api/home/userInfo/getUserInfo?page=1";
        if (!string.IsNullOrWhiteSpace(uuid)) url += $"&uuid={Uri.EscapeDataString(uuid)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", account.Cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", account.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://ff14risingstones.web.sdo.com/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Logger.Warn($"石之家接口 {(int)resp.StatusCode}：{Head(text)}");
            return null;
        }
        try { return JsonNode.Parse(text); }
        catch (Exception ex)
        {
            // 被指纹检测拦下时返回的是网页而不是 JSON
            Logger.Error($"石之家返回的不是 JSON：{Head(text)}", ex);
            return null;
        }
    }

    /// <summary>
    /// 读这个账号绑定的角色和它的房屋。
    ///
    /// 石之家全站只有四个房屋字段（house_info / house_info_publish / house_public /
    /// house_remain_day），没有任何进房时间，所以替代不了打卡：
    /// house_info 能告诉我们是哪套房，house_remain_day 只在快拆时才有值（个人页那行红字），
    /// 当成「快没时间了」的兜底告警用。
    /// </summary>
    public async Task<RisingStonesCharacter?> GetCharacterAsync(RisingStonesAccount account, string? uuid = null, CancellationToken ct = default)
    {
        var data = (await GetUserInfoAsync(account, uuid, ct))?["data"];
        var detail = data?["characterDetail"]?.AsArray()?.FirstOrDefault();
        if (data == null || detail == null) return null;

        var houseText = detail["house_info"]?.ToString() ?? "";
        int? remain = int.TryParse(detail["house_remain_day"]?.ToString(), out var d) ? d : null;
        var serverName = data["group_name"]?.ToString() ?? "";

        return new RisingStonesCharacter
        {
            Name = detail["character_name"]?.ToString() ?? data["character_name"]?.ToString() ?? "",
            ServerName = serverName,
            ServerId = GameData.AllServers.FirstOrDefault(x => x.Name == serverName)?.Id ?? 0,
            HouseText = houseText,
            House = ParsePlot(houseText),
            HouseRemainDay = remain,
        };
    }

    /// <summary>把「高脚孤丘22区29号-S」拆成房区/区号/房号；屏蔽或没房时返回 null</summary>
    public static (int Area, int Slot, int Id)? ParsePlot(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(?<area>.+?)(?<slot>\d+)区(?<id>\d+)号");
        if (!m.Success) return null;
        var area = Array.IndexOf(GameData.AreaNames, m.Groups["area"].Value);
        if (area < 0) return null;
        var slot = int.Parse(m.Groups["slot"].Value);
        var id = int.Parse(m.Groups["id"].Value);
        if (slot < 1 || slot > 30 || id < 1 || id > 60) return null;
        return (area, slot - 1, id);   // 界面上的区号从 1 起，内部从 0 起
    }

    private static string Head(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}

/// <summary>石之家上这个账号绑定的角色（一个账号只绑一个角色）</summary>
public class RisingStonesCharacter
{
    public string Name { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int ServerId { get; set; }
    /// <summary>原样的「高脚孤丘22区29号-S」；被隐私开关屏蔽时是「*已屏蔽*」或空</summary>
    public string HouseText { get; set; } = "";
    public (int Area, int Slot, int Id)? House { get; set; }
    /// <summary>拆除剩余天数，只在石之家认为该告警时才有值</summary>
    public int? HouseRemainDay { get; set; }
}

/// <summary>一个石之家账号的登录凭据（只存本机，不同步到云端）</summary>
public class RisingStonesAccount
{
    public string Cookie { get; set; } = "";
    public string UserAgent { get; set; } = "";
    /// <summary>登录后从接口读到的昵称，仅用于界面上区分多个账号</summary>
    public string Nickname { get; set; } = "";
    public DateTimeOffset LinkedAt { get; set; }
    /// <summary>Cookie 到期时间（石之家大约给一周）。到期后读不到数据，得重新登录</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>上次提醒「该重新登录了」的时间，用来限制成一天最多一条</summary>
    public DateTimeOffset? LastWarnedAt { get; set; }
    /// <summary>最近一次成功拉取的时间；长时间为空说明 Cookie 过期了</summary>
    public DateTimeOffset? LastOkAt { get; set; }
}
