using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace FF14HouseReminder.Services;

/// <summary>版本更新检查（读取 GitHub release）</summary>
public class UpdateService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.1.0";

    public string? LatestVersion { get; private set; }
    public string? ReleaseUrl { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public event Action? UpdateChecked;

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HousingApiClient.UserAgent);
    }

    public async Task CheckAsync(string checkUrl)
    {
        try
        {
            var release = await _http.GetFromJsonAsync<GitHubRelease>(checkUrl);
            if (release?.TagName == null) return;

            var latest = release.TagName.TrimStart('v', 'V');
            if (Version.TryParse(latest, out var lv) && Version.TryParse(CurrentVersion, out var cv))
            {
                LatestVersion = latest;
                ReleaseUrl = release.HtmlUrl;
                UpdateAvailable = lv > cv;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"检查更新失败：{ex.Message}");
        }
        UpdateChecked?.Invoke();
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }

    public void Dispose() => _http.Dispose();
}
