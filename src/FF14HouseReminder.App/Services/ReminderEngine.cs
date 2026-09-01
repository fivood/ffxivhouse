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

    /// <summary>
    /// 当前落在哪一档提前量里（最多一档）。
    /// 补签一个很旧的进屋日期时实际只剩 5 天，不该先冒出一条「还剩 10 天」。
    /// </summary>
    private static IEnumerable<int> CurrentLeadLevel(DateTimeOffset deadline, DateTimeOffset now)
    {
        for (var i = 0; i < DemolitionLeadDays.Length; i++)
        {
            var from = deadline.AddDays(-DemolitionLeadDays[i]);
            var to = i + 1 < DemolitionLeadDays.Length
                ? deadline.AddDays(-DemolitionLeadDays[i + 1])
                : deadline;
            if (now >= from && now < to) yield return DemolitionLeadDays[i];
        }
    }

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
        var watchDirty = false;

        foreach (var watch in _config.Config.WatchList.Where(w => w.Enabled))
        {
            // 落选押金返还：死线在公示期结束后 90 天，那时房子已从在售列表消失、
            // 阶段也早过了，只能按关注项自己记下的死线排期，不能跟着当前阶段算
            if (watch.DepositDeadline is { } deposit)
            {
                if (now >= deposit)
                {
                    watch.DepositDeadline = null;
                    watchDirty = true;
                }
                else if (settings.NotifyDepositDeadline)
                {
                    foreach (var h in settings.LeadHours)
                    {
                        Add2(ReminderType.DepositDeadline, h, deposit.AddHours(-h), deposit, watch.Key.ToString(),
                            "落选押金返还即将截止",
                            $"{watch.DisplayName} 若你上轮落选，押金返还期限（公示期结束后 90 天）将于 " +
                            $"{deposit.LocalDateTime:MM-dd HH:mm} 截止，逾期将不予返还！记得上线点门牌领回金币。");
                    }
                }
            }

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
                // 提前量早于现在但阶段尚未结束：立即提醒一次（用户新关注时常见）。
                // 去重位统一写 now——否则 24h/1h 都已过时会在同一刻弹出两条一模一样的
                var leadKey = leadHours?.ToString() ?? "x";
                if (fireAt <= now && anchorEnd > now) { fireAt = now; leadKey = "now"; }
                if (fireAt <= now.AddSeconds(-60)) return; // 阶段已过的不再排期
                var key = $"{keyPrefix}|{(int)type}|{anchorEnd:yyyyMMddHHmmss}|{leadKey}";
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
                    // 新一轮开抽：挂在申请期开始那一刻。挂在上一阶段结束的话，
                    // 到点时 GetPhase 已经翻页到申请期，原分支再也进不去，等于永远发不出
                    if (settings.NotifyNextEntryStart && watch.Mode == WatchMode.Planned)
                    {
                        Add(ReminderType.NextEntryStart, null, phase.PhaseEnd.AddDays(-LotteryCycle.EntryDays),
                            phase.PhaseEnd, watch.Key.ToString(),
                            "新一轮抽签开始",
                            $"{pos} 已开放抽签预约，申请期将于 {phase.PhaseEnd.LocalDateTime:MM-dd HH:mm} 截止，想去抽记得上线报名！");
                    }
                    break;

                case LotteryState.ResultsPeriod:
                    // 开奖：申请期一结束就进公示期，挂在公示期开始那一刻发
                    if (settings.NotifyResultsStart && watch.Mode == WatchMode.Participated)
                    {
                        Add(ReminderType.ResultsStart, null, phase.PhaseEnd.AddDays(-LotteryCycle.ResultsDays),
                            phase.PhaseEnd, watch.Key.ToString(),
                            "抽房结果已公布",
                            $"{pos} 已进入公示期，你参与抽签的房子开奖了，快去查看结果！" +
                            $"公示期将于 {phase.PhaseEnd.LocalDateTime:MM-dd HH:mm} 截止。");
                    }
                    // 确认归属死线对两种关注模式都提醒：已报名要去看结果/购入，计划抽也该知道本轮结果
                    if (settings.NotifyClaimDeadline)
                    {
                        foreach (var h in settings.LeadHours)
                        {
                            Add(ReminderType.ClaimDeadline, h, phase.PhaseEnd.AddHours(-h),
                                phase.PhaseEnd, watch.Key.ToString(),
                                "公示期即将截止（确认归属死线）",
                                $"{pos} 公示期将于 {phase.PhaseEnd.LocalDateTime:MM-dd HH:mm} 截止。" +
                                "中签请立即购入，逾期将失去资格并被扣除 50% 申请金！");
                        }
                    }
                    // 已报名的，把落选押金返还死线记在关注项上，房子下架后还能按它提醒
                    if (watch.Mode == WatchMode.Participated)
                    {
                        var depositDeadline = phase.PhaseEnd.AddDays(90);
                        if (watch.DepositDeadline != depositDeadline)
                        {
                            watch.DepositDeadline = depositDeadline;
                            watchDirty = true;
                        }
                    }
                    break;

                case LotteryState.Preparing:
                    // 「新一轮开抽」在申请期分支里发（见上），这里不排
                    break;
            }
        }

        // ── 炸房提醒（45 天未进房）──
        foreach (var home in _config.Config.Homes)
        {
            var homeKey = $"home:{home.Key}";
            var pos = $"{home.PositionText}（{home.Label}）";

            // 已炸房：旧家具保管 35 天死线
            if (home.DemolishedAt > 0)
            {
                var furnitureDeadline = home.FurnitureDeadline;
                foreach (var days in CurrentLeadLevel(furnitureDeadline, now))
                {
                    Add2(ReminderType.FurnitureDeadline, days, furnitureDeadline.AddDays(-days), furnitureDeadline, homeKey,
                        $"旧家具保管即将到期：还剩 {days} 天",
                        $"{pos} 已被拆除，旧家具由 NPC 保管 35 天，将于 {furnitureDeadline.LocalDateTime:MM-dd HH:mm} 到期！" +
                        "请尽快去住宅区的管理人处取回家具，逾期将被丢弃！");
                }
                if (now >= furnitureDeadline)
                {
                    Add2(ReminderType.FurnitureDeadline, 0, now, furnitureDeadline, homeKey,
                        "旧家具保管已到期",
                        $"{pos} 的旧家具保管 35 天期限已到！若还没取回，请立刻去管理人处确认！");
                }
                continue; // 炸房的不再做进房倒计时
            }

            if (home.LastEnteredAt <= 0) continue;
            var deadline = home.Deadline;

            foreach (var days in CurrentLeadLevel(deadline, now))
            {
                Add2(ReminderType.Demolition, days, deadline.AddDays(-days), deadline, homeKey,
                    $"炸房警告：还剩 {days} 天",
                    $"{pos} 已超过 {45 - days} 天未进屋！记得上线进一次屋（进入室内才算），" +
                    $"进屋后在「我的房产」里打卡。死线：{deadline.LocalDateTime:MM-dd HH:mm}");
            }
            // 已过期：立即提醒一次
            if (now >= deadline)
            {
                Add2(ReminderType.Demolition, 0, now, deadline, homeKey,
                    "炸房倒计时已到",
                    $"{pos} 已超过 45 天未进屋，可能已进入拆除流程！请立即上线进屋抢救！");
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

        if (watchDirty) _config.Save();

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
