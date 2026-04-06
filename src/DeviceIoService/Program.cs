using DeviceIoService;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows Service when launched by SCM; runs as console when interactive
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "DeviceIoService";
});

// Shared service state (UA connection, counters, control signals)
builder.Services.AddSingleton<DeviceStatus>();

// IoT Hub sender
builder.Services.AddSingleton<IoTHubSender>();

// OPC UA background worker
builder.Services.AddHostedService<OpcUaClientHostedService>();

var app = builder.Build();

// Health
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Status
app.MapGet("/status", (DeviceStatus status) => Results.Ok(new
{
    uaConnected = status.UaConnected,
    lastNotification = status.LastNotification,
    monitoredItemCount = status.MonitoredItemCount,
    iotHub = new
    {
        sendSuccess = status.SendSuccess,
        sendFailure = status.SendFailure
    }
}));

// Trigger OPC UA reconnect
app.MapPost("/opcua/reconnect", (DeviceStatus status) =>
{
    status.RequestReconnect();
    return Results.Accepted();
});

// Reload tag list from configuration without restart
app.MapPost("/tags/reload", (DeviceStatus status) =>
{
    status.RequestTagsReload();
    return Results.Accepted();
});

app.Run();
