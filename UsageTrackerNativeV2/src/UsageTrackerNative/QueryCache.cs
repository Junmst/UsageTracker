using System.Collections.Concurrent;

namespace UsageTrackerNative;

/// <summary>
/// 查询结果缓存层（L2），基于 ConcurrentDictionary + 滑动 TTL
/// </summary>
public sealed class QueryCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TodayTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryTtl = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 200;
    private DateTime _lastCleanup = DateTime.UtcNow;

    private sealed class CacheEntry
    {
        public object Value { get; }
        public DateTime ExpiresAt { get; set; }

        public CacheEntry(object value, DateTime expiresAt)
        {
            Value = value;
            ExpiresAt = expiresAt;
        }
    }

    /// <summary>
    /// 获取或添加缓存项（今日数据短 TTL）
    /// </summary>
    public T GetOrAddToday<T>(string key, Func<T> factory) where T : class
    {
        return GetOrAdd(key, factory, TodayTtl);
    }

    /// <summary>
    /// 获取或添加缓存项（历史数据长 TTL）
    /// </summary>
    public T GetOrAddHistory<T>(string key, Func<T> factory) where T : class
    {
        return GetOrAdd(key, factory, HistoryTtl);
    }

    /// <summary>
    /// 获取或添加缓存项（默认 TTL）
    /// </summary>
    public T GetOrAdd<T>(string key, Func<T> factory, TimeSpan? ttl = null) where T : class
    {
        var effectiveTtl = ttl ?? DefaultTtl;
        var now = DateTime.UtcNow;

        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            // 滑动刷新 TTL
            entry.ExpiresAt = now + effectiveTtl;
            return (T)entry.Value;
        }

        // 驱逐过期条目：如果超过上限，清理最旧的
        if (_entries.Count >= MaxEntries)
        {
            CleanupExpired();
        }

        // 低频定时清理：每 60 秒执行一次，避免低负载下过期条目永不清理
        if ((now - _lastCleanup).TotalSeconds > 60)
        {
            CleanupExpired();
            _lastCleanup = now;
        }

        var value = factory();
        _entries[key] = new CacheEntry(value, now + effectiveTtl);
        return value;
    }

    /// <summary>
    /// 使指定键的缓存失效
    /// </summary>
    public void Invalidate(string key)
    {
        _entries.TryRemove(key, out _);
    }

    /// <summary>
    /// 使指定前缀的所有缓存失效（如 "sessions:" 会清除 "sessions:2026-05-24" 等）
    /// </summary>
    public void InvalidateByPrefix(string prefix)
    {
        foreach (var kvp in _entries)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _entries.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// 清空所有缓存
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(kvp.Key, out _);
            }
        }
    }
}
