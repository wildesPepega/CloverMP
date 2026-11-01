using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CloverPitCoop.Net;

// Line-based TCP link for testing two game copies on one PC. The host accepts one client.
// Reads happen on a background thread into a queue; the game thread drains it with TryReceive.
// Steam relay replaces this for real play; the message format stays the same.
internal sealed class TcpLink : ILink
{
    private readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();
    private bool _isHost;
    private volatile bool _attached;
    private bool _joinReported;
    private bool _leftReported;
    private readonly object _writeLock = new object();
    private TcpListener _listener;
    private TcpClient _client;
    private StreamWriter _writer;
    private Thread _thread;
    private volatile bool _closed;

    public bool Connected => _writer != null && !_closed;
    public string Error { get; private set; }

    public static TcpLink Host(int port)
    {
        var link = new TcpLink { _isHost = true };
        link._listener = new TcpListener(IPAddress.Loopback, port);
        link._listener.Start();
        link._thread = new Thread(() =>
        {
            try
            {
                link.Attach(link._listener.AcceptTcpClient());
            }
            catch (Exception e)
            {
                link.Fail(e);
            }
        }) { IsBackground = true, Name = "coop-host" };
        link._thread.Start();
        return link;
    }

    public static TcpLink Join(string host, int port)
    {
        var link = new TcpLink();
        link._thread = new Thread(() =>
        {
            // The host copy may still be starting; retry for a while.
            for (int i = 0; i < 120 && !link._closed; i++)
            {
                try
                {
                    link.Attach(new TcpClient(host, port));
                    return;
                }
                catch (SocketException)
                {
                    Thread.Sleep(1000);
                }
                catch (Exception e)
                {
                    link.Fail(e);
                    return;
                }
            }
            link.Fail(new TimeoutException("could not connect to host"));
        }) { IsBackground = true, Name = "coop-join" };
        link._thread.Start();
        return link;
    }

    private void Attach(TcpClient client)
    {
        client.NoDelay = true;
        _client = client;
        NetworkStream stream = client.GetStream();
        var reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _attached = true;
        while (!_closed)
        {
            string line = reader.ReadLine();
            if (line == null)
            {
                _closed = true;
                break;
            }
            _inbox.Enqueue(line);
        }
    }

    private void Fail(Exception e)
    {
        if (!_closed)
        {
            Error = e.GetType().Name + ": " + e.Message;
        }
        _closed = true;
    }

    public void Send(string line)
    {
        if (_writer == null)
        {
            return;
        }
        lock (_writeLock)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }
    }

    // The localhost link carries exactly one client, always player 1.
    public bool TryReceive(out int fromPlayer, out string line)
    {
        fromPlayer = _isHost ? 1 : Coop.HostPlayer;
        return _inbox.TryDequeue(out line);
    }

    public void SendTo(int player, string line)
    {
        Send(line);
    }

    public bool TryJoined(out int player)
    {
        player = 1;
        if (_isHost && _attached && !_joinReported)
        {
            _joinReported = true;
            return true;
        }
        return false;
    }

    public bool TryLeft(out int player)
    {
        player = 1;
        if (_isHost && _joinReported && _closed && !_leftReported)
        {
            _leftReported = true;
            return true;
        }
        return false;
    }

    public void Poll()
    {
    }

    // The test link has no reconnecting.
    public bool TryInterrupted(out int player)
    {
        player = -1;
        return false;
    }

    public bool TryRejoined(out int player)
    {
        player = -1;
        return false;
    }

    public bool TryReconnected() => false;

    public void SendVolatile(string line)
    {
        Send(line);
    }

    public void Dispose()
    {
        _closed = true;
        try { _client?.Close(); } catch (Exception) { }
        try { _listener?.Stop(); } catch (Exception) { }
    }
}
