using System.Text.Json.Serialization;

public sealed record TelemetryMessage(
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("siteId")] string SiteId,
    [property: JsonPropertyName("assetId")] string AssetId,
    [property: JsonPropertyName("tags")] Dictionary<string, double> Tags
);

public sealed record AlarmCommand(
    Guid CommandId,
    DateTimeOffset Ts,
    string SiteId,
    string AssetId,
    string AlarmId,
    string Type,
    string UserId,
    string? Comment,
    DateTimeOffset? ShelveUntil
)
{
    public static AlarmCommand Ack(string siteId, string assetId, string alarmId, string userId, string? comment) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, siteId, assetId, alarmId, "Ack", userId, comment, null);

    public static AlarmCommand Shelve(string siteId, string assetId, string alarmId, string userId, DateTimeOffset until, string? comment) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, siteId, assetId, alarmId, "Shelve", userId, comment, until);

    public static AlarmCommand Unshelve(string siteId, string assetId, string alarmId, string userId, string? comment) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, siteId, assetId, alarmId, "Unshelve", userId, comment, null);
}
