using System.Net;
using System.Text.Json;
using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 本地数据接收服务（HttpListener，仅监听 127.0.0.1）。
/// 供卫月插件把游戏内抓到的本服房屋数据直接推送进来。
/// 两个端点用不着 Kestrel，走系统自带的 http.sys（loopback 前缀无需管理员权限）。
/// </summary>
public class LocalIngestServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ConfigService _config;
    private readonly DataStore _store;
    private HttpListener? _listener;

    /// <summary>运行状态描述（供 UI 显示）</summary>
    public string StatusText { get; private set; } = "未启动";
    public bool IsRunning { get; private set; }

    public event Action? StatusChanged;

    public LocalIngestServer(ConfigService config, DataStore store)
    {
        _config = config;
        _store = store;
        _store.DataUpdated += () => StatusChanged?.Invoke();
    }

    public Task StartAsync()
    {
        if (!_config.Config.General.LocalIngestEnabled)
        {
            StatusText = "已停用";
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        }

        var port = _config.Config.General.LocalIngestPort;
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _listener = listener;
            _ = Task.Run(() => AcceptLoopAsync(listener));

            IsRunning = true;
            StatusText = $"监听 127.0.0.1:{port}";
            Logger.Info($"本地直报服务已启动：127.0.0.1:{port}");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusText = "启动失败";
            Logger.Error($"本地直报服务启动失败（端口 {port}）", ex);
        }
        StatusChanged?.Invoke();
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }   // Stop() 时会抛，正常退出
            try
            {
                await HandleAsync(ctx);
            }
            catch (Exception ex)
            {
                Logger.Error("本地直报请求处理失败", ex);
                try { ctx.Response.StatusCode = 500; } catch { }
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        var method = ctx.Request.HttpMethod;

        if (path == "/api/ping" && method == "GET")
        {
            await WriteJsonAsync(ctx, 200, new { ok = true, app = "FF14HouseReminder" });
            return;
        }

        if (path != "/api/ingest" || method != "POST")
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        if (ctx.Request.Headers["X-Ingest-Token"] != _config.Config.General.LocalIngestToken)
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        IngestRequest? req;
        try
        {
            req = await JsonSerializer.DeserializeAsync<IngestRequest>(ctx.Request.InputStream, JsonOptions);
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        if (req?.Entries == null || req.Entries.Count == 0)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        _store.MergeLocal(req.Entries);
        Logger.Info($"收到本地直报 {req.Entries.Count} 条（来源 {req.Source}）");
        await WriteJsonAsync(ctx, 200, new { ok = true, count = req.Entries.Count });
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        IsRunning = false;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }
}
