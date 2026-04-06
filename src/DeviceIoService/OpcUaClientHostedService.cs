using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace DeviceIoService;

/// <summary>
/// Background service that connects to an OPC UA server (anonymous auth),
/// subscribes to configured tags, and forwards every value change to Azure IoT Hub.
/// Supports reconnect/resubscribe and live tag-list reload.
/// </summary>
public sealed class OpcUaClientHostedService : BackgroundService
{
    private readonly ILogger<OpcUaClientHostedService> _log;
    private readonly IConfiguration _cfg;
    private readonly IoTHubSender _sender;
    private readonly DeviceStatus _status;

    public OpcUaClientHostedService(
        ILogger<OpcUaClientHostedService> log,
        IConfiguration cfg,
        IoTHubSender sender,
        DeviceStatus status)
    {
        _log = log;
        _cfg = cfg;
        _sender = sender;
        _status = status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _status.UaConnected = false;
                _log.LogError(ex, "OPC UA session error; reconnecting in 10 s");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        var endpointUrl = _cfg["OpcUa:EndpointUrl"]
            ?? throw new InvalidOperationException("Missing OpcUa:EndpointUrl");
        var appName = _cfg["OpcUa:ApplicationName"] ?? "DeviceIoService";
        var autoAccept = _cfg.GetValue("OpcUa:Security:AutoAcceptUntrustedCertificates", true);

        // Build ApplicationConfiguration entirely in code (no UA XML config file needed)
        var appConfig = new ApplicationConfiguration
        {
            ApplicationName = appName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:{appName}",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "%LocalApplicationData%/DeviceIoService/pki/own",
                    SubjectName = $"CN={appName}"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "%LocalApplicationData%/DeviceIoService/pki/issuers"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "%LocalApplicationData%/DeviceIoService/pki/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "%LocalApplicationData%/DeviceIoService/pki/rejected"
                },
                AutoAcceptUntrustedCertificates = autoAccept,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 1024
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 }
        };

        await appConfig.Validate(ApplicationType.Client);

        if (autoAccept)
        {
            appConfig.CertificateValidator.CertificateValidation += (_, e) =>
            {
                _log.LogWarning("Auto-accepting server certificate: {Subject}", e.Certificate?.Subject);
                e.Accept = true;
            };
        }

        // Ensure client certificate exists
        var appInstance = new ApplicationInstance { ApplicationConfiguration = appConfig };
        await appInstance.CheckApplicationInstanceCertificate(false, 2048);

        _log.LogInformation("Connecting to OPC UA server: {Url}", endpointUrl);

        var endpoint = CoreClientUtils.SelectEndpoint(appConfig, endpointUrl, useSecurity: false);
        var endpointCfg = EndpointConfiguration.Create(appConfig);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, endpointCfg);

        using var session = await Session.Create(
            appConfig,
            configuredEndpoint,
            false,
            false,
            appName,
            60_000,
            new UserIdentity(new AnonymousIdentityToken()),
            null,
            stoppingToken);

        _status.UaConnected = true;
        _log.LogInformation("OPC UA session established");

        // Keep-alive monitoring
        session.KeepAlive += (_, e) =>
        {
            if (ServiceResult.IsBad(e.Status))
            {
                _log.LogWarning("OPC UA keep-alive bad status: {Status}", e.Status);
                _status.UaConnected = false;
            }
        };

        await RunSubscriptionLoopAsync(session, stoppingToken);

        _status.UaConnected = false;
    }

    private async Task RunSubscriptionLoopAsync(Session session, CancellationToken stoppingToken)
    {
        Subscription? subscription = null;

        async Task SetupSubscriptionAsync()
        {
            // Remove old subscription if present
            if (subscription is not null)
            {
                session.RemoveSubscription(subscription);
                subscription.Dispose();
            }

            subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = _cfg.GetValue("OpcUa:PublishingIntervalMs", 1000),
                KeepAliveCount = 10,
                LifetimeCount = 100
            };
            session.AddSubscription(subscription);
            subscription.Create();

            var tags = _cfg.GetSection("OpcUa:Tags").Get<List<TagConfig>>() ?? new List<TagConfig>();
            foreach (var tag in tags)
            {
                var item = new MonitoredItem(subscription.DefaultItem)
                {
                    DisplayName = $"{tag.SiteId}/{tag.AssetId}/{tag.TagName}",
                    StartNodeId = NodeId.Parse(tag.NodeId),
                    AttributeId = Attributes.Value,
                    SamplingInterval = _cfg.GetValue("OpcUa:SamplingIntervalMs", 1000),
                    QueueSize = 10,
                    DiscardOldest = true
                };

                // Capture tag config for closure
                var capturedTag = tag;
                item.Notification += (MonitoredItem monItem, MonitoredItemNotificationEventArgs args) =>
                {
                    _ = HandleNotificationAsync(monItem, capturedTag, stoppingToken);
                };

                subscription.AddItem(item);
            }

            subscription.ApplyChanges();
            _status.MonitoredItemCount = tags.Count;
            _log.LogInformation("Subscribed to {Count} tag(s)", tags.Count);
        }

        await SetupSubscriptionAsync();

        // Wait for stop, reconnect request, or tags reload
        while (!stoppingToken.IsCancellationRequested)
        {
            var reconnectTask = _status.WaitForReconnectRequestAsync();
            var reloadTask = _status.WaitForTagsReloadRequestAsync();
            var completedTask = await Task.WhenAny(
                reconnectTask,
                reloadTask,
                Task.Delay(Timeout.Infinite, stoppingToken));

            if (stoppingToken.IsCancellationRequested)
                break;

            if (completedTask == reconnectTask)
            {
                _log.LogInformation("Reconnect requested; exiting session loop");
                // Exit RunSessionAsync, which will reconnect
                return;
            }

            if (completedTask == reloadTask)
            {
                _log.LogInformation("Tags reload requested; rebuilding subscription");
                await SetupSubscriptionAsync();
            }
        }

        if (subscription is not null)
        {
            session.RemoveSubscription(subscription);
            subscription.Dispose();
        }
    }

    private async Task HandleNotificationAsync(MonitoredItem item, TagConfig tag, CancellationToken ct)
    {
        try
        {
            foreach (var value in item.DequeueValues())
            {
                _status.LastNotification = DateTimeOffset.UtcNow;

                var telemetry = new TagTelemetry(
                    Ts: DateTimeOffset.UtcNow,
                    SiteId: tag.SiteId,
                    AssetId: tag.AssetId,
                    Tag: tag.TagName,
                    Value: value.Value,
                    Quality: value.StatusCode.ToString(),
                    SourceTimestamp: new DateTimeOffset(value.SourceTimestamp, TimeSpan.Zero));

                await _sender.SendAsync(telemetry, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Error handling OPC UA notification for tag {Tag}", item.DisplayName);
        }
    }

    private sealed record TagConfig(string SiteId, string AssetId, string TagName, string NodeId);
}
