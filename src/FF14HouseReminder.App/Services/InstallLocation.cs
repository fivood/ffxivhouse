using System.IO;

namespace FF14HouseReminder.Services;

/// <summary>
/// 首次运行时把程序挪到用户目录。
///
/// 自动更新是「新版本覆盖旧文件」，所以程序得待在一个不用管理员权限就能写的地方。
/// 从下载文件夹直接双击运行、或者放进 Program Files，都会让更新在替换那一步失败。
/// </summary>
public static class InstallLocation
{
    private const string AppName = "FF14HouseReminder";

    /// <summary>推荐位置：%LocalAppData%\Programs\FF14HouseReminder</summary>
    public static string PreferredDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    public static string PreferredExe => Path.Combine(PreferredDir, AppName + ".exe");

    /// <summary>当前位置是否够用：能写，而且不在下载/临时目录里</summary>
    public static bool IsFine()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return true;
        var dir = Path.GetDirectoryName(exe)!;

        if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'),
                Path.GetFullPath(PreferredDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return true;

        // 下载目录和临时目录：用户随手一清就没了，别待在这儿
        foreach (var bad in new[]
                 {
                     Path.GetTempPath(),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                 })
        {
            if (bad.Length > 0 && Path.GetFullPath(dir)
                    .StartsWith(Path.GetFullPath(bad), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return CanWrite(dir);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>把自己复制到推荐位置并在那儿启动；返回是否已经交接出去（调用方应立即退出）</summary>
    public static bool MoveAndRestart()
    {
        try
        {
            Directory.CreateDirectory(PreferredDir);
            File.Copy(Environment.ProcessPath!, PreferredExe, true);
            TryCreateShortcut();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = PreferredExe,
                WorkingDirectory = PreferredDir,
                UseShellExecute = false,
            });
            Logger.Info($"已安装到 {PreferredExe}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("安装到用户目录失败", ex);
            return false;
        }
    }

    /// <summary>桌面快捷方式。用 WScript.Shell 免得为了一个 .lnk 引一整个库</summary>
    private static void TryCreateShortcut()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            var lnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "抽房了吗.lnk");
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = PreferredExe;
            sc.WorkingDirectory = PreferredDir;
            sc.Description = "FF14 房屋抽签与炸房提醒";
            sc.Save();
        }
        catch (Exception ex)
        {
            Logger.Warn($"创建桌面快捷方式失败：{ex.Message}");
        }
    }
}
