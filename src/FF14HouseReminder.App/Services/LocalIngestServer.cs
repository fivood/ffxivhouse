using System.Net;
using System.Text.Json;
using FF14HouseReminder.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FF14HouseReminder.Services;

/// <summary>
/// 本地数据接收服务（Kestrel，仅监听 127.0.0.1）。
/// 供卫月插件把游戏内抓到的本服房屋数据直接推送进来。
/// </summary>
public class LocalIngestServer : IDisposable
{
    private readonly ConfigService _config;
    private readonly DataStore _store;
    private WebApplication? _app;

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

    public async Task StartAsync()
    {
        if (!_config.Config.General.LocalIngestEnabled)
        {
            StatusText = "已停用";
            StatusChanged?.Invoke();
            return;
        }

        var port = _config.Config.General.LocalIngestPort;
        var token = _config.Config.General.LocalIngestToken;

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));

            var app = builder.Build();

            app.MapGet("/api/ping", () => Results.Json(new { ok = true, app = "FF14HouseReminder" }));

            app.MapPost("/api/ingest", async (HttpContext ctx) =>
            {
                if (!ctx.Request.Headers.TryGetValue("X-Ingest-Token", out var t) || t != token)
                    return Results.Unauthorized();

                IngestRequest? req;
                try
                {
                    req = await JsonSerializer.DeserializeAsync<IngestRequest>(ctx.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return Results.BadRequest();
                }
                if (req?.Entries == null || req.Entries.Count == 0)
                    return Results.BadRequest();

                _store.MergeLocal(req.Entries);
                Logger.Info($"收到本地直报 {req.Entries.Count} 条（来源 {req.Source}）");
                return Results.Json(new { ok = true, count = req.Entries.Count });
            });

            _app = app;
            await app.StartAsync();
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
    }

    public void Dispose()
    {
        if (_app != null)
        {
            try { _app.StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
            _app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
    }
}
