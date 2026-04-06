using System.Text.Json;
using Azure.Messaging.ServiceBus;

public interface ICommandBus
{
    Task EnqueueAsync(AlarmCommand cmd);
}

public sealed class ServiceBusCommandBus : ICommandBus
{
    private readonly ServiceBusSender _sender;

    public ServiceBusCommandBus(ServiceBusClient client, string queueName)
        => _sender = client.CreateSender(queueName);

    public Task EnqueueAsync(AlarmCommand cmd)
    {
        var msg = new ServiceBusMessage(JsonSerializer.Serialize(cmd))
        {
            ContentType = "application/json",
            MessageId = cmd.CommandId.ToString()
        };
        return _sender.SendMessageAsync(msg);
    }
}
