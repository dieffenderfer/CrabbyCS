using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ENet;

namespace MouseHouse.Net;

/// <summary>
/// ENet-based P2P transport. Supports 1 host + 1 client (visit code model).
/// Uses the same ENet protocol as the Godot version for wire compatibility.
/// </summary>
public class ENetTransport : INetworkTransport
{
    public bool IsConnected => _remotePeer?.State == PeerState.Connected;
    public bool IsHost { get; private set; }
    public string? RemoteName { get; private set; }

    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnConnectionFailed;
    public event Action<NetMessage>? OnMessageReceived;

    private Host? _host;
    private Peer? _remotePeer;
    private int _port;

    // Connection timeout
    private float _connectTimer;
    private const float ConnectTimeout = 10f;
    private bool _connecting;

    // Reconnect
    private string? _lastCode;
    private float _reconnectTimer;
    private bool _wasConnected;

    public void Host(int port)
    {
        Dispose();
        Library.Initialize();

        _port = port;
        IsHost = true;
        _host = new Host();
        var address = new Address { Port = (ushort)port };
        _host.Create(address, 1, 2); // 1 peer, 2 channels (reliable + unreliable)
    }

    public void Join(string visitCode)
    {
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(visitCode));
            var parts = decoded.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int port) || port < 1 || port > 65535)
            {
                OnConnectionFailed?.Invoke("Invalid visit code format.");
                return;
            }

            Dispose();
            Library.Initialize();

            _lastCode = visitCode;
            IsHost = false;
            _host = new Host();
            _host.Create();

            var address = new Address();
            address.SetHost(parts[0]);
            address.Port = (ushort)port;
            _remotePeer = _host.Connect(address, 2);
            _connecting = true;
            _connectTimer = ConnectTimeout;
        }
        catch (Exception ex)
        {
            OnConnectionFailed?.Invoke($"Failed to join: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        _wasConnected = false;
        _connecting = false;
        _remotePeer?.Disconnect(0);
        _remotePeer = null;
        RemoteName = null;
        Dispose();
        OnDisconnected?.Invoke();
    }

    public void Update(float delta)
    {
        if (_host == null) return;

        // Connection timeout
        if (_connecting)
        {
            _connectTimer -= delta;
            if (_connectTimer <= 0)
            {
                _connecting = false;
                OnConnectionFailed?.Invoke("Connection timed out.");
                Dispose();
                return;
            }
        }

        // Reconnect timer
        if (_reconnectTimer > 0)
        {
            _reconnectTimer -= delta;
            if (_reconnectTimer <= 0 && _lastCode != null)
            {
                Join(_lastCode);
                _lastCode = null; // Only try once
            }
        }

        // Poll events
        bool polled = false;
        while (!polled)
        {
            if (_host.CheckEvents(out Event netEvent) <= 0)
            {
                if (_host.Service(0, out netEvent) <= 0)
                    break;
                polled = true;
            }

            switch (netEvent.Type)
            {
                case EventType.Connect:
                    _connecting = false;
                    _wasConnected = true;
                    _remotePeer = netEvent.Peer;
                    OnConnected?.Invoke();
                    // Send our name
                    SendReliable(NetMessage.SendName("Player"));
                    break;

                case EventType.Disconnect:
                case EventType.Timeout:
                    RemoteName = null;
                    if (_wasConnected && !IsHost)
                    {
                        // Try reconnect once
                        _wasConnected = false;
                        _reconnectTimer = 2f;
                    }
                    OnDisconnected?.Invoke();
                    break;

                case EventType.Receive:
                    var data = new byte[netEvent.Packet.Length];
                    netEvent.Packet.CopyTo(data);
                    netEvent.Packet.Dispose();

                    var msg = NetMessage.Deserialize(data);
                    if (msg != null)
                    {
                        if (msg.Type == "name")
                            RemoteName = msg.Name;
                        else if (msg.Type == "kick")
                            Disconnect();
                        else
                            OnMessageReceived?.Invoke(msg);
                    }
                    break;
            }
        }
    }

    public void SendReliable(NetMessage message)
    {
        if (_remotePeer?.State != PeerState.Connected) return;
        var data = message.Serialize();
        var packet = default(Packet);
        packet.Create(data, PacketFlags.Reliable);
        _remotePeer.Value.Send(0, ref packet);
    }

    public void SendUnreliable(NetMessage message)
    {
        if (_remotePeer?.State != PeerState.Connected) return;
        var data = message.Serialize();
        var packet = default(Packet);
        packet.Create(data, PacketFlags.Unsequenced);
        _remotePeer.Value.Send(1, ref packet);
    }

    public string? GetVisitCode()
    {
        if (!IsHost) return null;
        var ip = GetLocalIP();
        var raw = $"{ip}:{_port}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
    }

    private static string GetLocalIP()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
                        return ip;
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        _host?.Flush();
        _host?.Dispose();
        _host = null;
        _remotePeer = null;
    }
}
