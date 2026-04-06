using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public sealed class TelemetryProcessorHostedService : BackgroundService
{
    private readonly ILogger<TelemetryProcessorHostedService> _log;
    private readonly EventProcessorClient _processor;
    private readonly AlarmStateStore _store;
    private readonly IDatabase _db;

    private const string AlarmId = "HighPressure";
    private const string TagName = "Discharge_Pressure_Psi";
    private const double Hi = 60.0;
    private const double Deadband = 2.0;

    public TelemetryProcessorHostedService(
        ILogger<TelemetryProcessorHostedService> log,
        Azure.Storage.Blobs.BlobContainerClient checkpointContainer,
        AlarmStateStore store,
        IDatabase db)
    {
        _log = log;
        _store = store;
        _db = db;

        checkpointContainer.CreateIfNotExists();

        _processor = new EventProcessorClient(
            checkpointContainer,
            Env("EVENTHUB_CONSUMER_GROUP"),
            Env("EVENTHUB_CONNECTION_STRING"),
            Env("EVENTHUB_NAME"));

        _processor.ProcessEventAsync += OnEvent;
        _processor.ProcessErrorAsync += args =>
        {
            _log.LogError(args.Exception, "EventHubs error. Partition={{PartitionId}} Operation={{Operation}}",
                args.PartitionId, args.Operation);
            return Task.CompletedTask;
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Starting Event Hubs telemetry processor...");
        await _processor.StartProcessingAsync(stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        _log.LogInformation("Stopping Event Hubs telemetry processor...");
        await _processor.StopProcessingAsync(stoppingToken);
    }

    private async Task OnEvent(ProcessEventArgs args)
    {
        if (args.Data is null) return;

        TelemetryMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<TelemetryMessage>(args.Data.EventBody.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Bad telemetry JSON");
            return;
        }
        if (msg is null) return;

        if (!msg.Tags.TryGetValue(TagName, out var value))
            return;

        var alarmKey = AlarmStateStore.AlarmKey(msg.SiteId, msg.AssetId, AlarmId);
        var now = msg.Ts;

        var state = (string?)await _db.HashGetAsync(alarmKey, "state") ?? "Normal";

        bool shouldActivate = value >= Hi;
        bool shouldClear = value <= (Hi - Deadband);

        string? transition = null;
        string newState = state;

        if (state == "Normal" && shouldActivate)
        {
            newState = "ActiveUnacked";
            transition = "Raised";
        }
        else if ((state == "ActiveUnacked" || state == "ActiveAcked") && shouldClear)
        {
            newState = "Normal";
            transition = "Cleared";
        }

        await _db.HashSetAsync(alarmKey, new[]
        {
            new HashEntry("siteId", msg.SiteId),
            new HashEntry("assetId", msg.AssetId),
            new HashEntry("alarmId", AlarmId),
            new HashEntry("lastValue", value.ToString("G")),
            new HashEntry("isConditionActive", shouldActivate ? "1" : "0")
        });

        if (transition is not null)
        {
            await _db.HashSetAsync(alarmKey, new[]
            {
                new HashEntry("state", newState),
                new HashEntry("lastTransition", transition),
                new HashEntry("lastTransitionTs", now.ToString("O"))
            });

            if (newState.StartsWith("Active", StringComparison.OrdinalIgnoreCase))
            {
                bool shelvedNow = await _store.IsShelvedNowAsync(msg.SiteId, msg.AssetId, AlarmId, DateTimeOffset.UtcNow);
                if (!shelvedNow)
                    await _store.UpsertActiveAsync(msg.SiteId, msg.AssetId, AlarmId, now);
            }
            else
            {
                await _store.RemoveFromActiveAsync(msg.SiteId, msg.AssetId, AlarmId);
            }

            _log.LogInformation("Alarm transition {{AlarmKey}}: {{Transition}} -> {{State}} value={{Value}}",
                alarmKey, transition, newState, value);
        }

        await args.UpdateCheckpointAsync(args.CancellationToken);
    }

    private static string Env(string key) =>
        Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing env var: {{key}};");
}
