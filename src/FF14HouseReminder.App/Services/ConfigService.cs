using System.IO;
using System.Text.Json;
using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>配置读写（%AppData%\FF14HouseReminder\config.json）</summary>
public class ConfigService
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FF14HouseReminder");

    private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
    private static readonly string RemindersPath = Path.Combine(DataDir, "reminders.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AppConfig Config { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                         ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取配置失败，使用默认配置", ex);
            Config = new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("保存配置失败", ex);
        }
    }

    public List<ScheduledReminder> LoadReminders()
    {
        try
        {
            if (File.Exists(RemindersPath))
            {
                return JsonSerializer.Deserialize<List<ScheduledReminder>>(File.ReadAllText(RemindersPath), JsonOptions)
                       ?? [];
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取提醒计划失败", ex);
        }
        return [];
    }

    public void SaveReminders(List<ScheduledReminder> reminders)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(RemindersPath, JsonSerializer.Serialize(reminders, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("保存提醒计划失败", ex);
        }
    }
}

/// <summary>简单文件日志</summary>
public static class Logger
{
    private static readonly string LogPath =
        Path.Combine(ConfigService.DataDir, "app.log");
    private static readonly object Lock = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.DataDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}" +
                       (ex != null ? Environment.NewLine + ex : "") + Environment.NewLine;
            lock (Lock) File.AppendAllText(LogPath, line);
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
