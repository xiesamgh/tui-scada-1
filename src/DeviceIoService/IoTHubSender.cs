using System.Text;
using System.Text.Json;
using Microsoft.Azure.Devices.Client;

namespace DeviceIoService;

/// <summary>
/// Sends one IoT Hub device-to-cloud message per tag value change.
/// </summary>
public sealed class IoTHubSender : IAsyncDisposable
{
    private readonly DeviceClient _client;
    private readonly DeviceStatus _status;
    private readonly ILogger<IoTHubSender> _log;

    public IoTHubSender(IConfiguration cfg, DeviceStatus status, ILogger<IoTHubSender> log)
    {
        var cs = cfg["IoTHub:DeviceConnectionString"]
                 ?? throw new InvalidOperationException("Missing IoTHub:DeviceConnectionString");

        _client = DeviceClient.CreateFromConnectionString(cs, TransportType.Mqtt);
        _status = status;
        _log = log;
    }

    public async Task SendAsync(TagTelemetry payload, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var msg = new Message(Encoding.UTF8.GetBytes(json))
            {
                ContentType = "application/json",
                ContentEncoding = "utf-8"
            };

            await _client.SendEventAsync(msg, ct);
            _status.IncrementSendSuccess();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status.IncrementSendFailure();
            _log.LogError(ex, "Failed to send IoT Hub message for tag {Tag}", payload.Tag);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.CloseAsync();
        _client.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// Telemetry payload sent to IoT Hub for each tag value change.
/// </summary>
public sealed record TagTelemetry(
    DateTimeOffset Ts,
    string SiteId,
    string AssetId,
    string Tag,
    object? Value,
    string Quality,
    DateTimeOffset SourceTimestamp
);
