using StackExchange.Redis;

public sealed class AlarmStateStore
{
    private readonly IDatabase _db;
    public AlarmStateStore(IDatabase db) => _db = db;

    public static string AlarmKey(string siteId, string assetId, string alarmId)
        => $"alarm:{{siteId}}:{{assetId}}:{{alarmId}}";

    private static string ActiveZSetKey(string siteId) => $"alarms:active:{{siteId}}";
    private static string ShelvedSetKey(string siteId) => $"alarms:shelved:{{siteId}}";

    public async Task<ActiveAlarmPage> GetActiveAlarmsPageAsync(string siteId, int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 10, 1000);
        page = Math.Max(page, 0);

        long start = (long)page * pageSize;
        long stop = start + pageSize - 1;

        RedisValue[] keys = await _db.SortedSetRangeByRankAsync(
            ActiveZSetKey(siteId),
            start,
            stop,
            Order.Descending);

        var items = new List<AlarmSummary>(keys.Length);
        foreach (var key in keys)
        {
            var h = await _db.HashGetAllAsync(key!);
            items.Add(AlarmSummary.FromHash((string)key!, h));
        }

        long total = await _db.SortedSetLengthAsync(ActiveZSetKey(siteId));
        return new ActiveAlarmPage(siteId, page, pageSize, total, items);
    }

    public Task UpsertActiveAsync(string siteId, string assetId, string alarmId, DateTimeOffset transitionTsUtc)
    {
        string key = AlarmKey(siteId, assetId, alarmId);
        double score = transitionTsUtc.ToUnixTimeMilliseconds();

        var tran = _db.CreateTransaction();
        _ = tran.SetRemoveAsync(ShelvedSetKey(siteId), key);
        _ = tran.SortedSetAddAsync(ActiveZSetKey(siteId), key, score);
        return tran.ExecuteAsync();
    }

    public Task RemoveFromActiveAsync(string siteId, string assetId, string alarmId)
        => _db.SortedSetRemoveAsync(ActiveZSetKey(siteId), AlarmKey(siteId, assetId, alarmId));

    public async Task SetShelvedAsync(string siteId, string assetId, string alarmId, DateTimeOffset until, string userId, string? comment)
    {
        string key = AlarmKey(siteId, assetId, alarmId);

        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(key, new[]
        {
            new HashEntry("siteId", siteId),
            new HashEntry("assetId", assetId),
            new HashEntry("alarmId", alarmId),

            new HashEntry("shelvedUntil", until.ToString("O")),
            new HashEntry("shelvedBy", userId),
            new HashEntry("shelvedTs", DateTimeOffset.UtcNow.ToString("O")),
            new HashEntry("shelveComment", comment ?? "")
        });

        _ = tran.SortedSetRemoveAsync(ActiveZSetKey(siteId), key);
        _ = tran.SetAddAsync(ShelvedSetKey(siteId), key);

        await tran.ExecuteAsync();
    }

    public async Task ClearShelvedAsync(string siteId, string assetId, string alarmId)
    {
        string key = AlarmKey(siteId, assetId, alarmId);

        var tran = _db.CreateTransaction();
        _ = tran.HashDeleteAsync(key, new RedisValue[] { "shelvedUntil", "shelvedBy", "shelvedTs", "shelveComment" });
        _ = tran.SetRemoveAsync(ShelvedSetKey(siteId), key);
        await tran.ExecuteAsync();
    }

    public async Task<bool> IsShelvedNowAsync(string siteId, string assetId, string alarmId, DateTimeOffset nowUtc)
    {
        string key = AlarmKey(siteId, assetId, alarmId);
        var untilStr = (string?)await _db.HashGetAsync(key, "shelvedUntil");
        if (string.IsNullOrWhiteSpace(untilStr)) return false;
        return DateTimeOffset.TryParse(untilStr, out var until) && until > nowUtc;
    }

    public sealed record ActiveAlarmPage(string SiteId, int Page, int PageSize, long Total, List<AlarmSummary> Items);

    public sealed record AlarmSummary(string Key, string? State, string? LastTransition, string? LastTransitionTs, string? AssetId, string? AlarmId)
    {
        public static AlarmSummary FromHash(string key, HashEntry[] h)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in h) d[(string)e.Name!] = (string)e.Value!;

            return new AlarmSummary(
                key,
                d.GetValueOrDefault("state"),
                d.GetValueOrDefault("lastTransition"),
                d.GetValueOrDefault("lastTransitionTs"),
                d.GetValueOrDefault("assetId"),
                d.GetValueOrDefault("alarmId"));
        }
    }
}
