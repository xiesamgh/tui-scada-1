namespace DeviceIoService;

/// <summary>
/// Thread-safe shared state used by REST endpoints and the OPC UA hosted service.
/// </summary>
public sealed class DeviceStatus
{
    private int _monitoredItemCount;
    private long _sendSuccess;
    private long _sendFailure;
    private bool _uaConnected;
    private DateTimeOffset? _lastNotification;
    private DateTimeOffset? _lastReconnectRequest;

    public bool UaConnected
    {
        get => Volatile.Read(ref _uaConnected);
        set => Volatile.Write(ref _uaConnected, value);
    }

    public DateTimeOffset? LastNotification
    {
        get { lock (this) return _lastNotification; }
        set { lock (this) _lastNotification = value; }
    }

    public int MonitoredItemCount
    {
        get => Volatile.Read(ref _monitoredItemCount);
        set => Volatile.Write(ref _monitoredItemCount, value);
    }

    public long SendSuccess => Interlocked.Read(ref _sendSuccess);
    public long SendFailure => Interlocked.Read(ref _sendFailure);

    public void IncrementSendSuccess() => Interlocked.Increment(ref _sendSuccess);
    public void IncrementSendFailure() => Interlocked.Increment(ref _sendFailure);

    // Reconnect request channel
    private TaskCompletionSource<bool> _reconnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals the OPC UA service to reconnect.</summary>
    public void RequestReconnect()
    {
        lock (this)
        {
            _lastReconnectRequest = DateTimeOffset.UtcNow;
            var prev = _reconnectTcs;
            _reconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            prev.TrySetResult(true);
        }
    }

    /// <summary>Returns a task that completes when a reconnect is requested.</summary>
    public Task WaitForReconnectRequestAsync()
    {
        lock (this) return _reconnectTcs.Task;
    }

    // Tags reload channel
    private TaskCompletionSource<bool> _reloadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals the OPC UA service to reload tags from configuration.</summary>
    public void RequestTagsReload()
    {
        lock (this)
        {
            var prev = _reloadTcs;
            _reloadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            prev.TrySetResult(true);
        }
    }

    /// <summary>Returns a task that completes when a tags reload is requested.</summary>
    public Task WaitForTagsReloadRequestAsync()
    {
        lock (this) return _reloadTcs.Task;
    }
}
