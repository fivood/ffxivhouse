using System.Net.Http;
using System.Net.Http.Json;
using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>售楼中心 API 客户端（house.ffxiv.cyou）</summary>
public class HousingApiClient : IDisposable
{
    public const string BaseUrl = "https://house.ffxiv.cyou";
    public const string UserAgent = "FF14HouseReminder/0.1.0 (+https://github.com/fivood/ffxivhouse)";

    private readonly HttpClient _http;

    public HousingApiClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>获取指定服务器当前在售房屋列表</summary>
    public async Task<List<HouseEntry>> GetSalesAsync(int serverId, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<HouseEntry>>(
            $"/api/sales?server={serverId}", ct);
        return result ?? [];
    }

    /// <summary>获取指定服务器数据全量更新时间</summary>
    public async Task<long> GetUpdateTimeAsync(int serverId, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<long>($"/api/update_time?server={serverId}", ct);
    }

    public void Dispose() => _http.Dispose();
}
