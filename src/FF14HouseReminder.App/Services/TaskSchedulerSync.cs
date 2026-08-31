using System.IO;
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
            foreach (var task in folder.Tasks.ToList())
            {
                if (!wanted.ContainsKey(task.Name))
                    folder.DeleteTask(task.Name, false);
            }

            // 创建/更新任务
            foreach (var (name, reminder) in wanted)
            {
                var existing = folder.Tasks[name];
                if (existing != null)
                {
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
        }
        catch (Exception ex)
        {
            Logger.Error("同步任务计划失败", ex);
        }
    }

    private static string TaskName(string reminderKey)
    {
        var hash = reminderKey.GetHashCode(StringComparison.Ordinal).ToString("X8");
        return $"remind_{hash}";
    }

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
