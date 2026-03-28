using System.Numerics;

namespace MouseHouse.Net;

/// <summary>
/// High-level multiplayer manager. All game code interacts with this,
/// never with the transport directly. Swap transport to switch between
/// ENet (online) and Offline (no networking) modes.
///
/// To disable multiplayer entirely: set Enabled = false or use OfflineTransport.
/// </summary>
public class MultiplayerManager
{
    /// <summary>Master switch — when false, all multiplayer UI and features are hidden.</summary>
    public bool Enabled { get; set; }

    public INetworkTransport Transport { get; private set; }
    public bool IsConnected => Transport.IsConnected;
    public bool IsHost => Transport.IsHost;
    public string? RemoteName => Transport.RemoteName;

    // Remote state (updated by incoming messages)
    public Vector2 RemotePosition { get; private set; }
    public string RemoteLocation { get; private set; } = "";
    public string RemoteActivity { get; private set; } = "";
    public Vector2 RemoteDesktopPosition { get; private set; }
    public string RemoteDesktopStatus { get; private set; } = "";
    public string RemoteDesktopState { get; private set; } = "";
    public bool RemoteWeatherRaining { get; private set; }

    // Events for game code to react to
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnConnectionFailed;
    public event Action<string, string>? OnChatReceived; // (sender, text)
    public event Action<string, string>? OnActivityStateReceived; // (activity, json)

    public MultiplayerManager(bool enabled = false)
    {
        Enabled = enabled;
        Transport = enabled ? CreateTransport() : new OfflineTransport();
        WireEvents();
    }

    /// <summary>
    /// Swap transport at runtime (e.g. go online/offline).
    /// </summary>
    public void SetTransport(INetworkTransport transport)
    {
        Transport.Dispose();
        UnwireEvents();
        Transport = transport;
        WireEvents();
    }

    public void Update(float delta)
    {
        if (!Enabled) return;
        Transport.Update(delta);
    }

    // --- Outgoing ---

    public void Host(int port = 7777)
    {
        if (!Enabled) return;
        Transport.Host(port);
    }

    public void Join(string visitCode)
    {
        if (!Enabled) return;
        Transport.Join(visitCode);
    }

    public void Disconnect()
    {
        Transport.Disconnect();
    }

    public string? GetVisitCode() => Transport.GetVisitCode();

    public void SendPosition(Vector2 position, string location)
    {
        if (!IsConnected) return;
        Transport.SendUnreliable(NetMessage.Position(position, location));
    }

    public void SendDesktopPosition(Vector2 normalizedPos)
    {
        if (!IsConnected) return;
        Transport.SendUnreliable(NetMessage.DesktopPosition(normalizedPos));
    }

    public void SendChat(string sender, string text)
    {
        if (!IsConnected) return;
        Transport.SendReliable(NetMessage.Chat(sender, text));
    }

    public void SendActivityChange(string activity)
    {
        if (!IsConnected) return;
        Transport.SendReliable(NetMessage.ActivityChange(activity));
    }

    public void SendActivityState(string activity, string json, bool reliable = true)
    {
        if (!IsConnected) return;
        var msg = NetMessage.ActivityState(activity, json);
        if (reliable)
            Transport.SendReliable(msg);
        else
            Transport.SendUnreliable(msg);
    }

    public void SendDesktopStatus(string text)
    {
        if (!IsConnected) return;
        Transport.SendReliable(NetMessage.DesktopStatus(text));
    }

    public void SendDesktopState(string stateName)
    {
        if (!IsConnected) return;
        Transport.SendReliable(NetMessage.DesktopState(stateName));
    }

    public void KickVisitor()
    {
        if (!IsConnected || !IsHost) return;
        Transport.SendReliable(NetMessage.Kick());
        Disconnect();
    }

    // --- Incoming ---

    private void HandleMessage(NetMessage msg)
    {
        switch (msg.Type)
        {
            case "position":
                RemotePosition = new Vector2(msg.X, msg.Y);
                RemoteLocation = msg.Activity ?? "";
                break;
            case "desktop_pos":
                RemoteDesktopPosition = new Vector2(msg.X, msg.Y);
                break;
            case "desktop_status":
                RemoteDesktopStatus = msg.Text ?? "";
                break;
            case "desktop_state":
                RemoteDesktopState = msg.State ?? "";
                break;
            case "chat":
                OnChatReceived?.Invoke(msg.Name ?? "?", msg.Text ?? "");
                break;
            case "activity_change":
                RemoteActivity = msg.Activity ?? "";
                break;
            case "activity_state":
                OnActivityStateReceived?.Invoke(msg.Activity ?? "", msg.Json ?? "");
                break;
            case "weather":
                RemoteWeatherRaining = msg.Flag;
                break;
        }
    }

    private void WireEvents()
    {
        Transport.OnConnected += () => OnConnected?.Invoke();
        Transport.OnDisconnected += () => OnDisconnected?.Invoke();
        Transport.OnConnectionFailed += s => OnConnectionFailed?.Invoke(s);
        Transport.OnMessageReceived += HandleMessage;
    }

    private void UnwireEvents()
    {
        // Events are re-wired on SetTransport; old delegates are GC'd with old transport
    }

    private static INetworkTransport CreateTransport()
    {
        try
        {
            // Try to create ENet transport
            return new ENetTransport();
        }
        catch
        {
            // ENet not available — fall back to offline
            return new OfflineTransport();
        }
    }
}
