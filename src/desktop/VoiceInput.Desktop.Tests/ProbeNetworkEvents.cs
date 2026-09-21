using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Http;
using System.Net.WebSockets;

internal sealed class ProbeNetworkEvents : EventListener
{
    private readonly object _gate = new();
    private readonly List<object> _events = [];
    private readonly long _origin = Stopwatch.GetTimestamp();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name is "System.Net.Sockets" or "System.Net.Security" or "System.Net.Http")
            EnableEvents(source, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        // Whitelist names and numeric fields only. Never enable private/raw network logging.
        var accepted = (data.EventSource.Name, data.EventName) switch
        {
            ("System.Net.Sockets", "ConnectStart" or "ConnectStop" or "ConnectFailed") => true,
            ("System.Net.Security", "HandshakeStart" or "HandshakeStop" or "HandshakeFailed") => true,
            ("System.Net.Http", "ConnectionEstablished") => true,
            _ => false,
        };
        if (!accepted || _gate is null) return;
        var values = new Dictionary<string, long>();
        if (data.PayloadNames is not null && data.Payload is not null)
            for (var i = 0; i < data.PayloadNames.Count; i++)
                if (data.PayloadNames[i] is "protocol" or "versionMajor" or "versionMinor" or "error")
                {
                    var number = Numeric(data.Payload[i]);
                    if (number is not null) values[data.PayloadNames[i]] = number.Value;
                }
        lock (_gate)
            if (_events.Count < 64)
                _events.Add(new { source = data.EventSource.Name, @event = data.EventName,
                    at_ms = Stopwatch.GetElapsedTime(_origin).TotalMilliseconds, values });
    }

    internal static long? Numeric(object? value) => value switch
    {
        byte number => number, short number => number, int number => number, long number => number,
        _ => null,
    };

    public object[] Snapshot() { lock (_gate) return _events.ToArray(); }

    public static async Task<object> ConnectionSettingsAsync(Uri endpoint)
    {
        using var defaults = new ClientWebSocket();
        var proxy = "unavailable";
        try
        {
            proxy = await Task.Run(() =>
            {
                try
                {
                    var destination = new UriBuilder(endpoint)
                    { Scheme = endpoint.Scheme == "wss" ? "https" : "http", Port = endpoint.IsDefaultPort ? -1 : endpoint.Port }.Uri;
                    var configured = HttpClient.DefaultProxy;
                    if (configured.IsBypassed(destination)) return "direct";
                    var route = configured.GetProxy(destination);
                    return route is null || route == destination ? "direct" : "proxy";
                }
                catch (Exception) { return "unavailable"; }
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { }
        return new
        {
            proxy_policy = "system_default",
            resolved_proxy = proxy,
            // These are documented defaults, not a claim to have read the live private socket.
            settings_source = "fresh_ClientWebSocket_defaults_and_DefaultProxy_lookup",
            http_version = defaults.Options.HttpVersion.ToString(),
            http_version_policy = defaults.Options.HttpVersionPolicy.ToString(),
            compression = defaults.Options.DangerousDeflateOptions is not null,
            keepalive_interval_seconds = defaults.Options.KeepAliveInterval.TotalSeconds,
            keepalive_timeout_seconds = defaults.Options.KeepAliveTimeout.TotalSeconds,
            socket_send_buffer_bytes = (int?)null,
            tcp_nodelay = (bool?)null,
            kernel_pending_bytes = (int?)null,
            transport_pending_bytes = (int?)null,
        };
    }
}
