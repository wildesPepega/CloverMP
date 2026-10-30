using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;
using Steamworks.Data;

namespace CloverPitCoop.Net;

// Steam relay transport (SteamNetworkingSockets through Valve's relays): no port forwarding,
// no VPN, works under the game's own appid because every player owns the game.
// The host opens a relay socket; clients connect to the host's SteamID.
internal sealed class SteamLink : ILink
{
    private const int VirtualPort = 7;

    private readonly ConcurrentQueue<(int, string)> _inbox = new ConcurrentQueue<(int, string)>();
    private readonly Queue<int> _joined = new Queue<int>();
    private readonly Queue<int> _left = new Queue<int>();
    private HostSocket _host;
    private ClientConnection _client;

    public string Error { get; private set; }

    public bool Connected => _host != null ? _host.Players.Count > 0 : _client != null && _client.IsUp;

    public static SteamLink Host()
    {
        SteamNetworkingUtils.InitRelayNetworkAccess();
        var link = new SteamLink();
        link._host = SteamNetworkingSockets.CreateRelaySocket<HostSocket>(VirtualPort);
        link._host.Link = link;
        return link;
    }

    public static SteamLink Join(SteamId host)
    {
        SteamNetworkingUtils.InitRelayNetworkAccess();
        var link = new SteamLink();
        link._client = SteamNetworkingSockets.ConnectRelay<ClientConnection>(host, VirtualPort);
        link._client.Link = link;
        return link;
    }

    public void Poll()
    {
        _host?.Receive();
        _client?.Receive();
    }

    public void Send(string line)
    {
        if (_host != null)
        {
            foreach (KeyValuePair<int, Connection> p in _host.Players)
            {
                p.Value.SendMessage(line);
            }
        }
        else
        {
            _client?.Connection.SendMessage(line);
        }
    }

    public void SendTo(int player, string line)
    {
        if (_host != null && _host.Players.TryGetValue(player, out Connection c))
        {
            c.SendMessage(line);
        }
    }

    public bool TryReceive(out int fromPlayer, out string line)
    {
        if (_inbox.TryDequeue(out (int, string) m))
        {
            fromPlayer = m.Item1;
            line = m.Item2;
            return true;
        }
        fromPlayer = -1;
        line = null;
        return false;
    }

    public bool TryJoined(out int player) => TryTake(_joined, out player);

    public bool TryLeft(out int player) => TryTake(_left, out player);

    private static bool TryTake(Queue<int> q, out int player)
    {
        if (q.Count > 0)
        {
            player = q.Dequeue();
            return true;
        }
        player = -1;
        return false;
    }

    public void Dispose()
    {
        _host?.Close();
        _client?.Close();
    }

    private static string Decode(IntPtr data, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private sealed class HostSocket : SocketManager
    {
        public SteamLink Link;
        public readonly Dictionary<int, Connection> Players = new Dictionary<int, Connection>();
        private readonly Dictionary<uint, int> _byConnection = new Dictionary<uint, int>();
        private int _nextPlayer = 1;

        public override void OnConnected(Connection connection, ConnectionInfo info)
        {
            base.OnConnected(connection, info);
            int player = _nextPlayer++;
            Players[player] = connection;
            _byConnection[connection.Id] = player;
            Link._joined.Enqueue(player);
            Plugin.Log.LogInfo($"[coop] steam: player {player} connected");
        }

        public override void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            base.OnDisconnected(connection, info);
            if (_byConnection.TryGetValue(connection.Id, out int player))
            {
                _byConnection.Remove(connection.Id);
                Players.Remove(player);
                Link._left.Enqueue(player);
                Plugin.Log.LogInfo($"[coop] steam: player {player} left ({info.EndReason})");
            }
        }

        public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (_byConnection.TryGetValue(connection.Id, out int player))
            {
                Link._inbox.Enqueue((player, Decode(data, size)));
            }
        }
    }

    private sealed class ClientConnection : ConnectionManager
    {
        public SteamLink Link;
        public bool IsUp;

        public override void OnConnected(ConnectionInfo info)
        {
            base.OnConnected(info);
            IsUp = true;
            Plugin.Log.LogInfo("[coop] steam: connected to host");
        }

        public override void OnDisconnected(ConnectionInfo info)
        {
            base.OnDisconnected(info);
            IsUp = false;
            Link.Error = "disconnected: " + info.EndReason;
            Plugin.Log.LogInfo($"[coop] steam: disconnected ({info.EndReason})");
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Link._inbox.Enqueue((Coop.HostPlayer, Decode(data, size)));
        }
    }
}
