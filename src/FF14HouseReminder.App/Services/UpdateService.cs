using System.IO;
using System.IO.Compression;
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
    /// <summary>安装包直链（走自己的域名中转，国内直连 GitHub 经常超时）</summary>
    public string? DownloadUrl { get; set; }
    public long DownloadSize { get; private set; }
    public bool UpdateAvailable { get; private set; }

    /// <summary>下载进度 0-100，-1 表示没在下</summary>
    public int Progress { get; private set; } = -1;
    public event Action<int>? ProgressChanged;

    public event Action? UpdateChecked;

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HousingApiClient.UserAgent);
    }

    public async Task CheckAsync(string checkUrl)
    {
        try
        {
            // 两种返回都认：自家中转（version/url）和 GitHub 原生（tag_name/html_url），
            // 老配置里存的还是 GitHub 那个地址
            var release = await _http.GetFromJsonAsync<ReleaseInfo>(checkUrl);
            var latest = (release?.Version ?? release?.TagName)?.TrimStart('v', 'V');
            if (latest == null) return;

            if (Version.TryParse(latest, out var lv) && Version.TryParse(CurrentVersion, out var cv))
            {
                LatestVersion = latest;
                ReleaseUrl = release!.Page ?? release.HtmlUrl;
                DownloadUrl = release.Url;
                DownloadSize = release.Size;
                UpdateAvailable = lv > cv;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"检查更新失败：{ex.Message}");
        }
        UpdateChecked?.Invoke();
    }

    /// <summary>
    /// 下载新版并交给它自己替换掉当前程序。
    ///
    /// 单文件 exe 运行中会被系统锁住，没法自己覆盖自己，所以必须由另一个进程来动手：
    /// 这里把下载下来的新 exe 用 --apply-update 拉起来，让它等本进程退出后覆盖过来。
    /// </summary>
    /// <returns>成功则返回 true，调用方随即退出程序</returns>
    public async Task<bool> DownloadAndApplyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(DownloadUrl)) return false;
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target)) return false;

        var dir = Path.Combine(ConfigService.DataDir, "update");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            var zipPath = Path.Combine(dir, "update.zip");
            await DownloadAsync(DownloadUrl!, zipPath, ct);

            var unpack = Path.Combine(dir, "unpacked");
            ZipFile.ExtractToDirectory(zipPath, unpack);
            var newExe = Directory.GetFiles(unpack, "FF14HouseReminder.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            // 单文件自包含的 exe 有几十 MB，明显偏小说明压缩包里不是我们要的东西
            if (newExe == null || new FileInfo(newExe).Length < 10 * 1024 * 1024)
            {
                Logger.Error("更新包里没找到可用的程序文件", new FileNotFoundException(unpack));
                return false;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = newExe,
                Arguments = $"--apply-update \"{target}\" {Environment.ProcessId}",
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("下载更新失败", ex);
            return false;
        }
        finally
        {
            Progress = -1;
            ProgressChanged?.Invoke(-1);
        }
    }

    private async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? DownloadSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            var pct = total > 0 ? (int)(done * 100 / total) : 0;
            if (pct != Progress) { Progress = pct; ProgressChanged?.Invoke(pct); }
        }
    }

    private class ReleaseInfo
    {
        // 自家中转的字段
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("page")] public string? Page { get; set; }
        // GitHub 原生的字段（老配置里存的是这个地址）
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }

    public void Dispose() => _http.Dispose();
}
