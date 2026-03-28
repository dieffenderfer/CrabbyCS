namespace MouseHouse.Net;

/// <summary>
/// Abstraction over the network transport. Implement this interface
/// to swap between ENet, WebSocket, or a null/offline transport.
/// All multiplayer code depends on this interface, never on ENet directly.
/// </summary>
public interface INetworkTransport : IDisposable
{
    bool IsConnected { get; }
    bool IsHost { get; }
    string? RemoteName { get; }

    void Host(int port);
    void Join(string visitCode);
    void Disconnect();
    void Update(float delta);

    // Send messages
    void SendReliable(NetMessage message);
    void SendUnreliable(NetMessage message);

    // Events
    event Action? OnConnected;
    event Action? OnDisconnected;
    event Action<string>? OnConnectionFailed;
    event Action<NetMessage>? OnMessageReceived;

    /// <summary>
    /// Generate a visit code for the current host session.
    /// Returns null if not hosting.
    /// </summary>
    string? GetVisitCode();
}
