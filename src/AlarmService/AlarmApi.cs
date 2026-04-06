public static class AlarmApi
{
    public static void MapAlarmEndpoints(this WebApplication app)
    {
        app.MapGet("/api/alarms/active",
            async (string siteId, int? page, int? pageSize, AlarmStateStore store) =>
            {
                int p = Math.Max(page ?? 0, 0);
                int ps = Math.Clamp(pageSize ?? 200, 10, 1000);
                return Results.Ok(await store.GetActiveAlarmsPageAsync(siteId, p, ps));
            });

        app.MapPost("/api/alarms/{siteId}/{assetId}/{alarmId}/ack",
            async (string siteId, string assetId, string alarmId, AckRequest body, ICommandBus bus) =>
            {
                var cmd = AlarmCommand.Ack(siteId, assetId, alarmId, body.UserId ?? "unknown", body.Comment);
                await bus.EnqueueAsync(cmd);
                return Results.Accepted($"/api/commands/{cmd.CommandId}", new { commandId = cmd.CommandId });
            });

        app.MapPost("/api/alarms/{siteId}/{assetId}/{alarmId}/shelve",
            async (string siteId, string assetId, string alarmId, ShelveRequest body, ICommandBus bus) =>
            {
                var until = body.ShelveUntil ?? DateTimeOffset.UtcNow.Add(body.Duration ?? TimeSpan.FromHours(2));
                var cmd = AlarmCommand.Shelve(siteId, assetId, alarmId, body.UserId ?? "unknown", until, body.Comment);
                await bus.EnqueueAsync(cmd);
                return Results.Accepted($"/api/commands/{cmd.CommandId}", new { commandId = cmd.CommandId, shelveUntil = until });
            });

        app.MapPost("/api/alarms/{siteId}/{assetId}/{alarmId}/unshelve",
            async (string siteId, string assetId, string alarmId, UnshelveRequest body, ICommandBus bus) =>
            {
                var cmd = AlarmCommand.Unshelve(siteId, assetId, alarmId, body.UserId ?? "unknown", body.Comment);
                await bus.EnqueueAsync(cmd);
                return Results.Accepted($"/api/commands/{cmd.CommandId}", new { commandId = cmd.CommandId });
            });
    }

    public record AckRequest(string? UserId, string? Comment);
    public record ShelveRequest(string? UserId, string? Comment, DateTimeOffset? ShelveUntil, TimeSpan? Duration);
    public record UnshelveRequest(string? UserId, string? Comment);
}
