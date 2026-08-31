namespace FF14HouseReminder.Models;

/// <summary>购买方式</summary>
public enum PurchaseType
{
    Unavailable = 0,
    /// <summary>先到先得</summary>
    FCFS = 1,
    /// <summary>抽签</summary>
    Lottery = 2
}

/// <summary>房屋用途限制</summary>
public enum RegionType
{
    Reserved = 0,
    /// <summary>仅限部队</summary>
    FC = 1,
    /// <summary>仅限个人</summary>
    Personal = 2
}

/// <summary>抽签状态（与售楼中心 API 一致）</summary>
public enum LotteryState
{
    /// <summary>未知/没有抽签信息（按首次发现时间推测）</summary>
    Unknown = 0,
    /// <summary>申请期，可供抽签</summary>
    Available = 1,
    /// <summary>公示期，可查看结果</summary>
    ResultsPeriod = 2,
    /// <summary>准备期，下轮开放</summary>
    Preparing = 3
}

/// <summary>关注模式</summary>
public enum WatchMode
{
    /// <summary>计划抽这套房</summary>
    Planned = 0,
    /// <summary>已报名参与</summary>
    Participated = 1
}

/// <summary>提醒类型</summary>
public enum ReminderType
{
    /// <summary>申请期截止前（快去报名）</summary>
    EntryDeadline = 0,
    /// <summary>进入公示期（开奖）</summary>
    ResultsStart = 1,
    /// <summary>公示期截止前（领房/领回押金死线）</summary>
    ClaimDeadline = 2,
    /// <summary>下轮申请期开始</summary>
    NextEntryStart = 3,
    /// <summary>炸房警告（45 天未进房）</summary>
    Demolition = 4
}

/// <summary>数据来源</summary>
public enum HouseDataSource
{
    /// <summary>售楼中心网站缓存</summary>
    Remote = 0,
    /// <summary>卫月插件本地直报</summary>
    Local = 1
}
