using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 提醒引擎：根据关注列表与房屋当前阶段计算提醒计划，
/// 到点触发（Toast + 外部推送），并同步 Windows 任务计划作为兜底。
/// </summary>
public class ReminderEngine
{
    private readonly ConfigService _config;
    private readonly DataStore _store;
    private readonly PushService _push;
    private readonly TaskSchedulerSync _taskSync;

    private List<ScheduledReminder> _scheduled = [];
    private readonly object _lock = new();

    /// <summary>炸房提醒提前量（天）</summary>
    private static readonly int[] DemolitionLeadDays = [10, 5, 1];

    public ReminderEngine(ConfigService config, DataStore store, PushService push, TaskSchedulerSync taskSync)
    {
        _config = config;
        _store = store;
        _push = push;
        _taskSync = taskSync;
        _scheduled = config.LoadReminders();
    }

    /// <summary>重新计算全部关注房屋的提醒计划</summary>
    public void Recompute()
    {
        var now = DateTimeOffset.Now;
        var settings = _config.Config.Reminders;
        var list = new List<ScheduledReminder>();

        foreach (var watch in _config.Config.WatchList.Where(w => w.Enabled))
        {
            var snapshot = _store.Get(watch.Key);
            if (snapshot == null) continue;

            var house = snapshot.Data;
            var phase = LotteryCycle.GetPhase(house, now);
            var stale = LotteryCycle.IsStale(snapshot, settings.StaleHoursWarning, now);
            var estimatedSuffix = phase.Estimated ? "\n（推测数据，建议登录游戏复核）" : "";
            var staleSuffix = stale ? "\n⚠ 数据已较久未更新，请以游戏内实际为准" : "";
            var suffix = estimatedSuffix + staleSuffix;
            var pos = $"{house.ServerName} {house.PositionText} [{house.SizeName}]";

            void Add(ReminderType type, int? leadHours, DateTimeOffset fireAt, DateTimeOffset anchorEnd, string keyPrefix, string title, string body)
            {
                // 提前量早于现在但阶段尚未结束：立即提醒一次（用户新关注时常见）
                if (fireAt <= now && anchorEnd > now) fireAt = now;
                if (fireAt <= now.AddSeconds(-60)) return; // 阶段已过的不再排期
                var key = $"{keyPrefix}|{(int)type}|{anchorEnd:yyyyMMddHHmmss}|{leadHours?.ToString() ?? "x"}";
                list.Add(new ScheduledReminder
                {
                    Key = key,
                    WatchKey = keyPrefix,
                    Type = type,
                    FireAt = fireAt,
                    Title = title,
                    Body = body + suffix
                });
            }

            switch (phase.State)
            {
                case LotteryState.Available:
                    if (settings.NotifyEntryDeadline && watch.Mode == WatchMode.Planned)
                    {
                        foreach (var h in settings.LeadHours)
                        {
                            Add(ReminderType.EntryDeadline, h, phase.PhaseEnd.AddHours(-h),
                                phase.PhaseEnd, watch.Key.ToString(),
                                "抽房报名即将截止",
                                $"{pos} 申请期将于 {phase.PhaseEnd.LocalDateTime:MM-dd HH:mm} 截止，想去抽记得上线报名！");
                        }
                    }
                    if (settings.NotifyResultsStart && watch.Mode == WatchMode.Participated)
                    {
                        Add(ReminderType.ResultsStart, null, phase.PhaseEnd,
                            phase.PhaseEnd, watch.Key.ToString(),
                            "抽房结果已公布",
                            $"{pos} 进入公示期，你参与抽签的房子开奖了，快去查看结果！");
                    }
                    break;

                case LotteryState.ResultsPeriod:
                    // 公示期死线对两种关注模式都提醒：已报名要去看结果/领钱，计划抽也该知道本轮结果
                    if (settings.NotifyClaimDeadline)
                    {
                        foreach (var h in settings.LeadHours)
                        {
                            Add(ReminderType.ClaimDeadline, h, phase.PhaseEnd.AddHours(-h),
                                phase.PhaseEnd, watch.Key.ToString(),
                                "领房/领回押金即将截止",
                                $"{pos} 公示期将于 {phase.PhaseEnd.LocalDateTime:MM-dd HH:mm} 截止。" +
                                "中签请尽快购入；落选记得领回押金，逾期会有损失！");
                        }
                    }
                    break;

                case LotteryState.Preparing:
                    if (settings.NotifyNextEntryStart && watch.Mode == WatchMode.Planned)
                    {
                        Add(ReminderType.NextEntryStart, null, phase.PhaseEnd,
                            phase.PhaseEnd, watch.Key.ToString(),
                            "新一轮抽签开始",
                            $"{pos} 预计于 {phase.PhaseEnd.LocalDateTime:MM-dd HH:mm} 开放抽签预约，想去抽记得上线！");
                    }
                    break;
            }
        }

        // ── 炸房提醒（45 天未进房）──
        foreach (var home in _config.Config.Homes)
        {
            if (home.LastEnteredAt <= 0) continue;
            var deadline = home.Deadline;
            var homeKey = $"home:{home.Key}";
            var pos = $"{home.PositionText}（{home.Label}）";

            foreach (var days in DemolitionLeadDays)
            {
                Add2(ReminderType.Demolition, days, deadline.AddDays(-days), deadline, homeKey,
                    $"炸房警告：还剩 {days} 天",
                    $"{pos} 已超过 {45 - days} 天未进房！记得上线进一次房（进入室内才算），" +
                    $"进房后在「我的房产」里打卡。死线：{deadline.LocalDateTime:MM-dd HH:mm}");
            }
            // 已过期：立即提醒一次
            if (now >= deadline)
            {
                Add2(ReminderType.Demolition, 0, now, deadline, homeKey,
                    "炸房倒计时已到",
                    $"{pos} 已超过 45 天未进房，可能已进入拆除流程！请立即上线进房抢救！");
            }
        }

        // 局部函数（炸房用，无 suffix）
        void Add2(ReminderType type, int? leadDays, DateTimeOffset fireAt, DateTimeOffset anchorEnd, string keyPrefix, string title, string body)
        {
            if (fireAt <= now && anchorEnd > now) fireAt = now;
            if (fireAt <= now.AddSeconds(-60)) return;
            var key = $"{keyPrefix}|{(int)type}|{anchorEnd:yyyyMMddHHmmss}|{leadDays?.ToString() ?? "x"}";
            list.Add(new ScheduledReminder
            {
                Key = key,
                WatchKey = keyPrefix,
                Type = type,
                FireAt = fireAt,
                Title = title,
                Body = body
            });
        }

        lock (_lock)
        {
            // 保留已触发标记
            var old = _scheduled.Where(s => s.Fired).ToDictionary(s => s.Key);
            foreach (var item in list)
            {
                if (old.TryGetValue(item.Key, out var prev)) item.Fired = prev.Fired;
            }
            _scheduled = list;
            _config.SaveReminders(_scheduled);
        }

        _taskSync.Sync(list.Where(r => !r.Fired).ToList());

        // 立即检查一次（覆盖"提前量已过但阶段未结束"的即时提醒）
        _ = FireDueAsync();
    }

    /// <summary>检查并触发到点的提醒</summary>
    public async Task FireDueAsync()
    {
        List<ScheduledReminder> due;
        var now = DateTimeOffset.Now;
        lock (_lock)
        {
            due = _scheduled.Where(r => !r.Fired && r.FireAt <= now).ToList();
            foreach (var r in due) r.Fired = true;
            if (due.Count > 0) _config.SaveReminders(_scheduled);
        }

        foreach (var reminder in due)
        {
            Logger.Info($"触发提醒 [{reminder.Type}] {reminder.Title} {reminder.WatchKey}");
            await _push.SendAllAsync(reminder.Title, reminder.Body);
            MarkFired(reminder.Key);
        }
    }

    /// <summary>在配置的关注列表中标记提醒已触发（供 --notify 模式去重）</summary>
    public void MarkFired(string reminderKey)
    {
        var watch = _config.Config.WatchList
            .FirstOrDefault(w => reminderKey.StartsWith(w.Key.ToString() + "|"));
        if (watch != null && watch.FiredReminders.Add(reminderKey))
            _config.Save();
    }

    public bool IsFired(string reminderKey)
    {
        lock (_lock) return _scheduled.Any(r => r.Key == reminderKey && r.Fired);
    }

    /// <summary>每套房最近的下一条提醒（供 UI 显示倒计时）</summary>
    public IReadOnlyList<ScheduledReminder> GetPending()
    {
        lock (_lock) return _scheduled.Where(r => !r.Fired).OrderBy(r => r.FireAt).ToList();
    }
}
