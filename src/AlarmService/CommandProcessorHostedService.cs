using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public sealed class CommandProcessorHostedService : BackgroundService
{
    private readonly ILogger<CommandProcessorHostedService> _log;
    private readonly ServiceBusProcessor _processor;
    private readonly AlarmStateStore _store;
    private readonly IDatabase _db;

    public CommandProcessorHostedService(
        ILogger<CommandProcessorHostedService> log,
        ServiceBusClient sb,
        AlarmStateStore store,
        IDatabase db)
    {
        _log = log;
        _store = store;
        _db = db;

        _processor = sb.CreateProcessor(Env("SERVICEBUS_COMMAND_QUEUE"), new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4
        });

        _processor.ProcessMessageAsync += OnMessage;
        _processor.ProcessErrorAsync += args =>
        {
            _log.LogError(args.Exception, "ServiceBus error. Entity={{EntityPath}} ErrorSource={{ErrorSource}}",
                args.EntityPath, args.ErrorSource);
            return Task.CompletedTask;
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Starting Service Bus command processor...");
        await _processor.StartProcessingAsync(stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        _log.LogInformation("Stopping Service Bus command processor...");
        await _processor.StopProcessingAsync(stoppingToken);
    }

    private async Task OnMessage(ProcessMessageEventArgs args)
    {
        AlarmCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<AlarmCommand>(args.Message.Body.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Bad command JSON");
            await args.DeadLetterMessageAsync(args.Message, "BadPayload", "Cannot deserialize command");
            return;
        }
        if (cmd is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "BadPayload", "Null command");
            return;
        }

        string dedupeKey = $"cmd:{{cmd.CommandId}}";
        if (await _db.StringGetAsync(dedupeKey) == "1")
        {
            await args.CompleteMessageAsync(args.Message);
            return;
        }

        string alarmKey = AlarmStateStore.AlarmKey(cmd.SiteId, cmd.AssetId, cmd.AlarmId);

        switch (cmd.Type)
        {
            case "Ack":
                await ApplyAckAsync(alarmKey, cmd);
                break;

            case "Shelve":
                if (cmd.ShelveUntil is null)
                {
                    await args.DeadLetterMessageAsync(args.Message, "BadPayload", "ShelveUntil required");
                    return;
                }
                await _store.SetShelvedAsync(cmd.SiteId, cmd.AssetId, cmd.AlarmId, cmd.ShelveUntil.Value, cmd.UserId, cmd.Comment);
                break;

            case "Unshelve":
                await _store.ClearShelvedAsync(cmd.SiteId, cmd.AssetId, cmd.AlarmId);
                await MaybeReAddToActiveAsync(alarmKey, cmd);
                break;

            default:
                await args.DeadLetterMessageAsync(args.Message, "BadPayload", $"Unknown command type: {{cmd.Type}};");
                return;
        }

        await _db.StringSetAsync(dedupeKey, "1", TimeSpan.FromDays(7));
        await args.CompleteMessageAsync(args.Message);
    }

    private async Task ApplyAckAsync(string alarmKey, AlarmCommand cmd)
    {
        var state = (string?)await _db.HashGetAsync(alarmKey, "state") ?? "Normal";

        if (state == "ActiveUnacked")
        {
            await _db.HashSetAsync(alarmKey, new[]
            {
                new HashEntry("state", "ActiveAcked"),
                new HashEntry("ackedBy", cmd.UserId),
                new HashEntry("ackedTs", cmd.Ts.ToString("O")),
                new HashEntry("ackComment", cmd.Comment ?? ""),
                new HashEntry("lastTransition", "Acked"),
                new HashEntry("lastTransitionTs", DateTimeOffset.UtcNow.ToString("O"))
            });
        }
    }

    private async Task MaybeReAddToActiveAsync(string alarmKey, AlarmCommand cmd)
    {
        var isActive = (string?)await _db.HashGetAsync(alarmKey, "isConditionActive");
        if (isActive == "1")
            await _store.UpsertActiveAsync(cmd.SiteId, cmd.AssetId, cmd.AlarmId, DateTimeOffset.UtcNow);
        else
            await _store.RemoveFromActiveAsync(cmd.SiteId, cmd.AssetId, cmd.AlarmId);
    }

    private static string Env(string key) =>
        Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing env var: {{key}};");
}
