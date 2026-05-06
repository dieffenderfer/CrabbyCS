namespace MouseHouse.Net;

/// <summary>
/// Null transport: no networking, no dependencies. Used when multiplayer
/// is disabled or ENet is not available. All sends are silently dropped.
/// </summary>
public class OfflineTransport : INetworkTransport
{
    public bool IsConnected => false;
    public bool IsHost => false;
    public string? RemoteName => null;

    // Required by INetworkTransport even though OfflineTransport never raises
    // them — connection / disconnection / message events only fire on real
    // transports. Suppress the unused-event warning for this stub.
#pragma warning disable CS0067
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnConnectionFailed;
    public event Action<NetMessage>? OnMessageReceived;
#pragma warning restore CS0067

    public void Host(int port)
        => OnConnectionFailed?.Invoke("Multiplayer is not available in offline mode.");

    public void Join(string visitCode)
        => OnConnectionFailed?.Invoke("Multiplayer is not available in offline mode.");

    public void Disconnect() { }
    public void Update(float delta) { }
    public void SendReliable(NetMessage message) { }
    public void SendUnreliable(NetMessage message) { }
    public string? GetVisitCode() => null;
    public void Dispose() { }
}
