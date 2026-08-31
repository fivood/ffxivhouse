using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FF14HouseReminder.Services;

/// <summary>Windows Toast 通知</summary>
public class ToastService
{
    public event Action<string?>? Activated; // 参数为 watchKey（可能为 null）

    public void Initialize()
    {
        try
        {
            ShortcutHelper.EnsureShortcut();
        }
        catch (Exception ex)
        {
            Logger.Error("创建开始菜单快捷方式失败（不影响使用）", ex);
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated += args =>
            {
                var query = args.Argument;
                Activated?.Invoke(string.IsNullOrEmpty(query) ? null : query);
            };
        }
        catch (Exception ex)
        {
            Logger.Error("注册 Toast 激活回调失败", ex);
        }
    }

    public void Show(string title, string body, string? watchKey = null)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(body);
            if (!string.IsNullOrEmpty(watchKey))
                builder.AddArgument("watch", watchKey);
            builder.Show();
            Logger.Info($"Toast 已弹出：{title}");
        }
        catch (Exception ex)
        {
            Logger.Error("弹出 Toast 失败", ex);
        }
    }
}

/// <summary>
/// 在开始菜单创建快捷方式（WScript.Shell 动态 COM，安全），
/// 让未打包程序的 Toast 通知有稳定的身份。
/// </summary>
internal static class ShortcutHelper
{
    public static void EnsureShortcut()
    {
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "FF14HouseReminder.lnk");

        var exePath = Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, "FF14HouseReminder.exe");

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exePath)!;
        shortcut.Save();
    }
}
