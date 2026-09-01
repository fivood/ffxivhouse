using System.IO;
using System.Security.Cryptography;
using System.Text;
using FF14HouseReminder.Models;
using Microsoft.Win32.TaskScheduler;

namespace FF14HouseReminder.Services;

/// <summary>
/// 将待触发提醒同步为 Windows 任务计划（程序未运行时也能弹出提醒）。
/// 任务动作：FF14HouseReminder.exe --notify "提醒Key"
/// </summary>
public class TaskSchedulerSync
{
    private const string FolderName = "FF14HouseReminder";
    private const string DailyName = "daily_refresh";

    public void Sync(List<ScheduledReminder> pending)
    {
        try
        {
            using var ts = new TaskService();
            var folder = ts.GetFolder(FolderName) ?? ts.RootFolder.CreateFolder(FolderName);

            var exePath = Environment.ProcessPath
                          ?? Path.Combine(AppContext.BaseDirectory, "FF14HouseReminder.exe");

            var wanted = pending
                // 两分钟内的即时提醒由程序内引擎直接触发，避免任务计划重复弹一次
                .Where(r => r.FireAt > DateTimeOffset.Now.AddMinutes(2))
                .ToDictionary(r => TaskName(r.Key), r => r);

            // 删除失效任务
            var existingNames = folder.Tasks.Select(t => t.Name).ToHashSet();
            foreach (var task in folder.Tasks.ToList())
            {
                if (task.Name != DailyName && !wanted.ContainsKey(task.Name))
                    folder.DeleteTask(task.Name, false);
            }

            // 创建/更新任务
            foreach (var (name, reminder) in wanted)
            {
                if (existingNames.Contains(name))
                {
                    var existing = folder.Tasks.First(t => t.Name == name);
                    var trigger = existing.Definition.Triggers.OfType<TimeTrigger>().FirstOrDefault();
                    if (trigger != null && trigger.StartBoundary == reminder.FireAt.LocalDateTime)
                        continue; // 已是最新
                    folder.DeleteTask(name, false);
                }

                var def = ts.NewTask();
                def.RegistrationInfo.Description = $"FF14 抽房提醒：{reminder.Title}";
                def.Triggers.Add(new TimeTrigger(reminder.FireAt.LocalDateTime));
                def.Actions.Add(new ExecAction(exePath, $"--notify \"{reminder.Key}\"",
                    Path.GetDirectoryName(exePath)));
                def.Settings.Enabled = true;
                def.Settings.AllowDemandStart = false;
                def.Settings.StartWhenAvailable = true; // 错过时间补触发
                def.Settings.DisallowStartIfOnBatteries = false;
                folder.RegisterTaskDefinition(name, def);
            }

            EnsureDailyRefresh(ts, folder, exePath);
        }
        catch (Exception ex)
        {
            Logger.Error("同步任务计划失败", ex);
        }
    }

    /// <summary>
    /// 每天叫醒一次程序重排提醒：Recompute 只排到之后两个阶段（约两周），
    /// 程序长期不开的话队列会随周期推进见底，这条负责续上。
    /// </summary>
    private static void EnsureDailyRefresh(TaskService ts, TaskFolder folder, string exePath)
    {
        var existing = folder.Tasks.FirstOrDefault(t => t.Name == DailyName);
        if (existing?.Definition.Actions.OfType<ExecAction>().FirstOrDefault()?.Path == exePath) return;
        if (existing != null) folder.DeleteTask(DailyName, false);

        var def = ts.NewTask();
        def.RegistrationInfo.Description = "FF14 抽房提醒：每日刷新提醒计划（无窗口）";
        def.Triggers.Add(new DailyTrigger { StartBoundary = DateTime.Today.AddHours(9), DaysInterval = 1 });
        def.Actions.Add(new ExecAction(exePath, "--refresh", Path.GetDirectoryName(exePath)));
        def.Settings.StartWhenAvailable = true;   // 关机错过就开机后补跑
        def.Settings.DisallowStartIfOnBatteries = false;
        def.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(5);
        folder.RegisterTaskDefinition(DailyName, def);
    }

    // 不能用 string.GetHashCode：它每个进程都重新随机化，
    // 同一条提醒换个进程算出来就是另一个任务名，等于每次启动把任务全删了重建
    private static string TaskName(string reminderKey) =>
        "remind_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(reminderKey)))[..8];

    public void ClearAll()
    {
        try
        {
            using var ts = new TaskService();
            var folder = ts.GetFolder(FolderName);
            if (folder != null)
            {
                foreach (var task in folder.Tasks.ToList())
                    folder.DeleteTask(task.Name, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("清理任务计划失败", ex);
        }
    }
}
