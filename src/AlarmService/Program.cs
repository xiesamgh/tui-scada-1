using Azure.Messaging.ServiceBus;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(Env("REDIS_CONNECTION_STRING")));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

// Storage for checkpoints
builder.Services.AddSingleton(_ =>
    new Azure.Storage.Blobs.BlobContainerClient(
        Env("BLOB_STORAGE_CONNECTION_STRING"),
        Env("BLOB_CONTAINER_NAME")));

// Service Bus
builder.Services.AddSingleton(_ => new ServiceBusClient(Env("SERVICEBUS_CONNECTION_STRING")));
builder.Services.AddSingleton<ICommandBus>(sp =>
    new ServiceBusCommandBus(
        sp.GetRequiredService<ServiceBusClient>(),
        Env("SERVICEBUS_COMMAND_QUEUE"))); // alarm-commands

// Alarm store
builder.Services.AddSingleton<AlarmStateStore>();

// Background processors
builder.Services.AddHostedService<TelemetryProcessorHostedService>();
builder.Services.AddHostedService<CommandProcessorHostedService>();

var app = builder.Build();

app.MapAlarmEndpoints();

app.Run();

static string Env(string key) =>
    Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing env var: {key}");
