using FF14HouseReminder.Models;

namespace FF14HouseReminder.Services;

/// <summary>
/// 数据中心：合并网站 API 与本地直报数据。
/// 同一套房：本地直报的 LastSeen 更新（或相等）时优先使用本地数据。
/// </summary>
public class DataStore
{
    private readonly object _lock = new();
    private readonly Dictionary<HouseKey, HouseSnapshot> _remote = new();
    private readonly Dictionary<HouseKey, HouseSnapshot> _local = new();

    /// <summary>数据发生变化时触发（可能来自后台线程）</summary>
    public event Action? DataUpdated;

    /// <summary>本地直报最后收到数据的时间</summary>
    public DateTimeOffset? LastLocalIngestAt { get; private set; }

    public void MergeRemote(int serverId, List<HouseEntry> entries)
    {
        lock (_lock)
        {
            // 该服旧数据中本次未出现的，说明已售出/下架，移除
            var incoming = entries.Select(e => e.Key).ToHashSet();
            foreach (var key in _remote.Keys.Where(k => k.Server == serverId && !incoming.Contains(k)).ToList())
                _remote.Remove(key);

            foreach (var entry in entries)
            {
                _remote[entry.Key] = new HouseSnapshot
                {
                    Data = entry,
                    Source = HouseDataSource.Remote,
                    FetchedAt = DateTimeOffset.Now
                };
            }
        }
        DataUpdated?.Invoke();
    }

    public void MergeLocal(List<HouseEntry> entries)
    {
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                // 忽略无效条目（如插件测试推送）
                if (entry.Server <= 0 || entry.Price <= 0 || entry.ID <= 0) continue;

                entry.LastSeen = DateTimeOffset.Now.ToUnixTimeSeconds();
                _local[entry.Key] = new HouseSnapshot
                {
                    Data = entry,
                    Source = HouseDataSource.Local,
                    FetchedAt = DateTimeOffset.Now
                };
            }
            LastLocalIngestAt = DateTimeOffset.Now;
        }
        DataUpdated?.Invoke();
    }

    /// <summary>获取某套房的合并视图（无数据返回 null）</summary>
    public HouseSnapshot? Get(HouseKey key)
    {
        lock (_lock)
        {
            var hasLocal = _local.TryGetValue(key, out var local);
            var hasRemote = _remote.TryGetValue(key, out var remote);

            if (hasLocal && hasRemote)
                return local!.Data.LastSeen >= remote!.Data.LastSeen ? local : remote;
            if (hasLocal) return local;
            if (hasRemote) return remote;
            return null;
        }
    }

    /// <summary>获取某服务器全部在售房屋（合并视图）</summary>
    public List<HouseSnapshot> GetServerSales(int serverId)
    {
        lock (_lock)
        {
            var keys = _remote.Keys.Where(k => k.Server == serverId)
                .Concat(_local.Keys.Where(k => k.Server == serverId))
                .ToHashSet();
            return keys.Select(Get).Where(s => s != null).Cast<HouseSnapshot>().ToList();
        }
    }

    public List<int> GetKnownServers()
    {
        lock (_lock)
        {
            return _remote.Keys.Select(k => k.Server)
                .Concat(_local.Keys.Select(k => k.Server))
                .Distinct().ToList();
        }
    }
}
