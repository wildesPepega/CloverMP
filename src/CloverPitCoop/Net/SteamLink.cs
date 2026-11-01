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
//
// Built for bad connections:
// - A connected link survives 30 s without traffic (Steam retransmits reliable messages itself).
// - Sends go through a queue: when Steam's send buffer is full the message waits instead of
//   being lost, and the buffer is large enough for a whole run on a slow upload.
// - If a connection does break, a client keeps trying to come back for 2 minutes and the host
//   keeps its player number (by SteamID) for 1 minute. What the client missed is sent again by
//   the co-op layer (RESUME).
internal sealed class SteamLink : ILink
{
    private const int VirtualPort = 7;
    private const int ConnectedTimeoutMs = 30000;
    private const int InitialTimeoutMs = 20000;
    private const int SendBufferBytes = 4 * 1024 * 1024;
    private const int SendRateMaxBytes = 2 * 1024 * 1024;
    private const int ReasonLeft = 1000;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HostGrace = TimeSpan.FromMinutes(1);

    private readonly ConcurrentQueue<(int, string)> _inbox = new ConcurrentQueue<(int, string)>();
    private readonly Queue<int> _joined = new Queue<int>();
    private readonly Queue<int> _left = new Queue<int>();
    private readonly Queue<int> _interrupted = new Queue<int>();
    private readonly Queue<int> _rejoined = new Queue<int>();
    private readonly Dictionary<int, Queue<string>> _hostOutbox = new Dictionary<int, Queue<string>>();
    private readonly Queue<string> _clientOutbox = new Queue<string>();
    private HostSocket _host;
    private ClientConnection _client;
    private SteamId _hostId;
    private DateTime? _downSince;
    private DateTime? _retryAt;
    private bool _reconnected;
    private bool _everUp;
    private bool _disposed;

    public string Error { get; private set; }

    public bool Connected => _host != null ? _host.Players.Count > 0 : _client != null && _client.IsUp;

    private static void Configure()
    {
        SteamNetworkingUtils.InitRelayNetworkAccess();
        // Facepunch's names: ConnectionTimeout is the one for connecting (TimeoutInitial),
        // Timeout the one for an established connection (TimeoutConnected).
        SteamNetworkingUtils.ConnectionTimeout = InitialTimeoutMs;
        SteamNetworkingUtils.Timeout = ConnectedTimeoutMs;
        SteamNetworkingUtils.SendBufferSize = SendBufferBytes;
        SteamNetworkingUtils.SendRateMax = SendRateMaxBytes;
    }

    // Closed on purpose by the other side (Dispose below), not a connection that broke.
    private static bool ClosedOnPurpose(ConnectionInfo info)
    {
        return info.State == ConnectionState.ClosedByPeer
            && info.EndReason >= NetConnectionEnd.App_Min && info.EndReason <= NetConnectionEnd.App_Max;
    }

    public static SteamLink Host()
    {
        Configure();
        var link = new SteamLink();
        link._host = SteamNetworkingSockets.CreateRelaySocket<HostSocket>(VirtualPort);
        link._host.Link = link;
        return link;
    }

    public static SteamLink Join(SteamId host)
    {
        Configure();
        var link = new SteamLink { _hostId = host };
        link.ConnectClient();
        return link;
    }

    private void ConnectClient()
    {
        _client = SteamNetworkingSockets.ConnectRelay<ClientConnection>(_hostId, VirtualPort);
        _client.Link = this;
    }

    public void Poll()
    {
        _host?.Receive();
        _client?.Receive();
        if (_host != null)
        {
            FlushHost();
            ExpireHostGrace();
        }
        else if (_client != null)
        {
            if (_client.IsUp)
            {
                Flush(_clientOutbox, _client.Connection);
            }
            RetryClient();
        }
    }

    private void RetryClient()
    {
        DateTime now = DateTime.UtcNow;
        if (_downSince.HasValue && Error == null && now - _downSince.Value > GiveUpAfter)
        {
            Error = "could not reconnect to the host";
            _retryAt = null;
            return;
        }
        if (_retryAt.HasValue && now >= _retryAt.Value)
        {
            _retryAt = null;
            Plugin.Log.LogInfo("[coop] steam: trying to reconnect");
            ConnectClient();
        }
    }

    private void FlushHost()
    {
        foreach (KeyValuePair<int, Queue<string>> box in _hostOutbox)
        {
            if (_host.Players.TryGetValue(box.Key, out Connection c))
            {
                Flush(box.Value, c);
            }
        }
    }

    // Keeps order; stops at the first message Steam cannot take right now and tries again next frame.
    private static void Flush(Queue<string> box, Connection connection)
    {
        while (box.Count > 0)
        {
            Result r = connection.SendMessage(box.Peek());
            if (r == Result.LimitExceeded)
            {
                return;
            }
            if (r != Result.OK)
            {
                Plugin.Log.LogWarning($"[coop] steam: send failed ({r})");
                if (r == Result.NoConnection || r == Result.InvalidState)
                {
                    return;
                }
            }
            box.Dequeue();
        }
    }

    public void Send(string line)
    {
        if (_host != null)
        {
            foreach (int player in new List<int>(_host.Players.Keys))
            {
                SendTo(player, line);
            }
        }
        else if (_client != null && _client.IsUp)
        {
            _clientOutbox.Enqueue(line);
            Flush(_clientOutbox, _client.Connection);
        }
    }

    public void SendTo(int player, string line)
    {
        if (_host == null || !_host.Players.TryGetValue(player, out Connection c))
        {
            return;
        }
        if (!_hostOutbox.TryGetValue(player, out Queue<string> box))
        {
            box = _hostOutbox[player] = new Queue<string>();
        }
        box.Enqueue(line);
        Flush(box, c);
    }

    public void SendVolatile(string line)
    {
        if (_host != null)
        {
            foreach (Connection c in _host.Players.Values)
            {
                c.SendMessage(line, SendType.Unreliable);
            }
        }
        else if (_client != null && _client.IsUp)
        {
            _client.Connection.SendMessage(line, SendType.Unreliable);
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

    public bool TryInterrupted(out int player) => TryTake(_interrupted, out player);

    public bool TryRejoined(out int player) => TryTake(_rejoined, out player);

    public bool TryReconnected()
    {
        bool r = _reconnected;
        _reconnected = false;
        return r;
    }

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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_host != null)
        {
            foreach (Connection c in _host.Players.Values)
            {
                c.Close(false, ReasonLeft, "session ended");
            }
            _host.Close();
        }
        if (_client != null)
        {
            _client.Connection.Close(false, ReasonLeft, "left");
            _client.Close();
        }
    }

    private void ExpireHostGrace()
    {
        foreach (int player in _host.GraceOver(DateTime.UtcNow))
        {
            _hostOutbox.Remove(player);
            _left.Enqueue(player);
        }
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
        private readonly Dictionary<ulong, int> _bySteamId = new Dictionary<ulong, int>();
        private readonly Dictionary<int, DateTime> _lost = new Dictionary<int, DateTime>();
        private int _nextPlayer = 1;

        public override void OnConnected(Connection connection, ConnectionInfo info)
        {
            base.OnConnected(connection, info);
            ulong steamId = info.Identity.IsSteamId ? info.Identity.SteamId.Value : 0;
            bool known = steamId != 0 && _bySteamId.ContainsKey(steamId);
            int player = known ? _bySteamId[steamId] : _nextPlayer++;
            if (steamId != 0)
            {
                _bySteamId[steamId] = player;
            }

            // Back within the grace period, or the old connection has not even timed out here yet.
            bool back = _lost.Remove(player) | Players.ContainsKey(player);
            if (Players.TryGetValue(player, out Connection old))
            {
                _byConnection.Remove(old.Id);
                old.Close();
            }
            Players[player] = connection;
            _byConnection[connection.Id] = player;
            Link._hostOutbox.Remove(player);
            (back ? Link._rejoined : Link._joined).Enqueue(player);
            Plugin.Log.LogInfo($"[coop] steam: player {player} {(back ? "reconnected" : "connected")}");
        }

        public override void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            base.OnDisconnected(connection, info);
            if (!_byConnection.TryGetValue(connection.Id, out int player))
            {
                return;
            }
            _byConnection.Remove(connection.Id);
            Players.Remove(player);
            Link._hostOutbox.Remove(player);
            if (ClosedOnPurpose(info))
            {
                Link._left.Enqueue(player);
                Plugin.Log.LogInfo($"[coop] steam: player {player} left ({info.EndReason})");
                return;
            }
            _lost[player] = DateTime.UtcNow + HostGrace;
            Link._interrupted.Enqueue(player);
            Plugin.Log.LogInfo($"[coop] steam: player {player} connection lost ({info.EndReason}), keeping the slot");
        }

        public List<int> GraceOver(DateTime now)
        {
            var over = new List<int>();
            foreach (KeyValuePair<int, DateTime> l in _lost)
            {
                if (now >= l.Value)
                {
                    over.Add(l.Key);
                }
            }
            foreach (int player in over)
            {
                _lost.Remove(player);
            }
            return over;
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
            Link._downSince = null;
            // Retries before the very first connection are no reconnect: the host greets a new player.
            Link._reconnected = Link._everUp;
            Link._everUp = true;
        }

        public override void OnDisconnected(ConnectionInfo info)
        {
            base.OnDisconnected(info);
            IsUp = false;
            Plugin.Log.LogInfo($"[coop] steam: disconnected ({info.State}, {info.EndReason})");
            if (Link._disposed || !ReferenceEquals(Link._client, this))
            {
                return;
            }
            Link._clientOutbox.Clear();
            if (ClosedOnPurpose(info))
            {
                // Nothing to come back to.
                Link.Error = "the host closed the session";
                return;
            }
            Link._downSince ??= DateTime.UtcNow;
            Link._retryAt = DateTime.UtcNow + RetryDelay;
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Link._inbox.Enqueue((Coop.HostPlayer, Decode(data, size)));
        }
    }
}
