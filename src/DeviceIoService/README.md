# DeviceIoService

An on-site Windows Service (.NET 8) that:
- Connects to an OPC UA server (anonymous authentication)
- Subscribes to configured tags and forwards **every value change** to Azure IoT Hub
- Exposes REST endpoints for health checks, status, reconnect, and tag reload

## Architecture

```
OPC UA Server ──── OpcUaClientHostedService ──── IoTHubSender ──── Azure IoT Hub
                                │
                          DeviceStatus (shared state)
                                │
                    ASP.NET Core Minimal API (REST endpoints)
```

## Prerequisites

- .NET 8 SDK (Windows)
- Access to an OPC UA server
- Azure IoT Hub device connection string

## Configuration

Edit `appsettings.json` (or set environment variables) before running:

| Key | Description |
|-----|-------------|
| `OpcUa:EndpointUrl` | OPC UA server URL, e.g. `opc.tcp://192.168.1.10:4840` |
| `OpcUa:ApplicationName` | Client application name (used in UA certificates) |
| `OpcUa:PublishingIntervalMs` | Subscription publishing interval in milliseconds (default: 1000) |
| `OpcUa:SamplingIntervalMs` | Monitored item sampling interval in milliseconds (default: 1000) |
| `OpcUa:Security:AutoAcceptUntrustedCertificates` | `true` for dev/on-site without PKI setup |
| `OpcUa:Tags` | Array of `{ SiteId, AssetId, TagName, NodeId }` |
| `IoTHub:DeviceConnectionString` | IoT Hub device connection string |
| `Urls` | Kestrel listen address (default: `http://localhost:5080`) |

### Override with environment variables

```powershell
$env:OpcUa__EndpointUrl = "opc.tcp://192.168.1.10:4840"
$env:IoTHub__DeviceConnectionString = "HostName=...;DeviceId=...;SharedAccessKey=..."
```

## IoT Hub Message Format

One message is sent per tag value change:

```json
{
  "ts": "2026-04-06T12:00:00.000Z",
  "siteId": "site-01",
  "assetId": "pump-01",
  "tag": "Discharge_Pressure_Psi",
  "value": 61.2,
  "quality": "Good",
  "sourceTimestamp": "2026-04-06T12:00:00.000Z"
}
```

## REST Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | Liveness check — returns `{ "status": "ok" }` |
| GET | `/status` | UA connection state, last notification time, monitored item count, IoT Hub send counters |
| POST | `/opcua/reconnect` | Trigger OPC UA session reconnect |
| POST | `/tags/reload` | Reload tag list from configuration without restart |

## Run Locally (console mode)

```bash
cd src/DeviceIoService
dotnet run
```

The service listens on `http://localhost:5080` by default.

## Build Self-Contained Executable

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish\DeviceIoService
```

## Install as a Windows Service

After publishing, install using `sc.exe`:

```powershell
sc.exe create DeviceIoService binPath= "C:\Services\DeviceIoService\DeviceIoService.exe" start= auto
sc.exe description DeviceIoService "OPC UA to Azure IoT Hub forwarder"
sc.exe start DeviceIoService
```

Or using PowerShell:

```powershell
New-Service -Name DeviceIoService `
            -BinaryPathName "C:\Services\DeviceIoService\DeviceIoService.exe" `
            -DisplayName "DeviceIoService" `
            -Description "OPC UA to Azure IoT Hub forwarder" `
            -StartupType Automatic
Start-Service DeviceIoService
```

## Stop and Remove the Service

```powershell
Stop-Service DeviceIoService
sc.exe delete DeviceIoService
```

## Reconnect Behavior

The service automatically reconnects after session failures with a 10-second delay.  
You can also trigger an immediate reconnect via the REST endpoint:

```bash
curl -X POST http://localhost:5080/opcua/reconnect
```

## Reload Tags Without Restart

To add or remove tags, update `appsettings.json` and call:

```bash
curl -X POST http://localhost:5080/tags/reload
```

The subscription will be rebuilt with the new tag list; the service does **not** restart.
