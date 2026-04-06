# AlarmService (single process)

Hosts:
- REST API for ACK/Shelve/Unshelve and reading active alarms (newest-first)
- Event Hubs consumer (telemetry -> alarm state machine -> Redis)
- Service Bus queue consumer (`alarm-commands`) (commands -> Redis)

Active alarms are cached per site in Redis as a ZSET: `alarms:active:{siteId}` ordered by last transition time (newest first).
Shelved alarms are hidden by default (removed from the active ZSET).

## Required environment variables

- REDIS_CONNECTION_STRING
- EVENTHUB_CONNECTION_STRING
- EVENTHUB_NAME
- EVENTHUB_CONSUMER_GROUP (e.g. alarm-engine)
- BLOB_STORAGE_CONNECTION_STRING
- BLOB_CONTAINER_NAME (e.g. eh-checkpoints)
- SERVICEBUS_CONNECTION_STRING
- SERVICEBUS_COMMAND_QUEUE=alarm-commands

## Run

```bash
cd src/AlarmService
dotnet run
```

## API

- GET `/api/alarms/active?siteId=denver-01&page=0&pageSize=200`
- POST `/api/alarms/{siteId}/{assetId}/{alarmId}/ack`
- POST `/api/alarms/{siteId}/{assetId}/{alarmId}/shelve`
- POST `/api/alarms/{siteId}/{assetId}/{alarmId}/unshelve`

## Demo alarm

This sample hardcodes one demo alarm:
- alarmId: `HighPressure`
- tag name: `Discharge_Pressure_Psi`
- hi=60, deadband=2

Replace with your real alarm definitions/configuration.
