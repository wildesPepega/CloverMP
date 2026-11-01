using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using UnityEngine;

namespace CloverPitCoop.Net;

// Discord Rich Presence for the CloverMP Discord application. Talks to the local Discord client
// over its IPC pipe (\\.\pipe\discord-ipc-N): frames of [opcode int32][length int32][JSON].
// Opcode 0 = handshake, 1 = command. No Discord SDK or extra DLL needed.
//
// Shows: coins, clover tickets, deposited / debt, rounds left to the deadline, and in co-op the
// party size. Checked every 0.5 s and sent at most every 1.5 s, only when something changed;
// the local Discord client throttles further on its own and keeps the latest. If Discord is not
// running it retries quietly.
internal static class DiscordPresence
{
    private const string ClientId = "1565629453562941560";
    private const string LargeImage = "clovermp";
    private const float BuildInterval = 0.5f;
    private const double SendInterval = 1.5;

    public static bool Enabled = true;

    private static Thread _thread;
    private static volatile string _pending;
    private static volatile bool _quit;
    private static string _lastBuilt;
    private static float _nextBuild;
    private static long _startUnix;
    private static readonly AutoResetEvent Wake = new AutoResetEvent(false);

    public static string Status { get; private set; } = "starting";

    public static void Start()
    {
        if (_thread != null)
        {
            return;
        }
        _startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _thread = new Thread(Run) { IsBackground = true, Name = "coop-discord" };
        _thread.Start();
    }

    public static void Stop()
    {
        _quit = true;
        Wake.Set();
    }

    // Main thread, every frame: rebuild the activity from the game every 2 s.
    public static void Tick()
    {
        if (Time.unscaledTime < _nextBuild)
        {
            return;
        }
        _nextBuild = Time.unscaledTime + BuildInterval;
        string activity = Enabled ? BuildActivity() : "null";
        if (activity != _lastBuilt)
        {
            _lastBuilt = activity;
            _pending = activity;
            Wake.Set();
        }
    }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        _nextBuild = 0f;
    }

    // ---- what is shown ----

    private static string BuildActivity()
    {
        string details;
        string state;
        GameplayMaster.GamePhase phase = GameplayMaster.GetGamePhase();
        if (GameplayMaster.instance == null || GameplayData.Instance == null || !CoopMenu.InGame())
        {
            details = "In the menu";
            state = null;
        }
        else if (phase == GameplayMaster.GamePhase.death)
        {
            details = "Run over";
            state = $"Reached deadline {GameplayData.DebtIndexGet() + 1}";
        }
        else
        {
            BigInteger coins = GameplayData.CoinsGet();
            long tickets = GameplayData.CloverTicketsGet();
            details = $"{Short(coins)} {(coins == 1 ? "coin" : "coins")} · {Short(tickets)} {(tickets == 1 ? "ticket" : "tickets")}";
            int rounds = GameplayData.RoundsLeftToDeadline();
            state = $"Deadline: {Short(GameplayData.DepositGet())} / {Short(GameplayData.DebtGet())} · "
                + (rounds <= 0 ? "deadline now" : rounds == 1 ? "1 round left" : $"{rounds} rounds left");
            if (Coop.MachineOwner >= 0 && Coop.Role != Role.Off)
            {
                details += Coop.MachineOwner == Coop.Self ? " · at the machine" : " · watching";
            }
        }

        var sb = new StringBuilder("{");
        sb.Append("\"details\":").Append(Json(details));
        if (state != null)
        {
            sb.Append(",\"state\":").Append(Json(state));
        }
        sb.Append(",\"timestamps\":{\"start\":").Append(_startUnix).Append('}');
        string large = Coop.Role == Role.Off ? $"CloverMP {Plugin.Version} · solo" : $"CloverMP {Plugin.Version} · co-op";
        sb.Append(",\"assets\":{\"large_image\":").Append(Json(LargeImage)).Append(",\"large_text\":").Append(Json(large)).Append('}');
        if (Coop.Role != Role.Off)
        {
            int size = Social.AllPlayers().Count;
            string party = SteamSession.CurrentLobby.HasValue ? SteamSession.CurrentLobby.Value.Id.ToString() : "clovermp";
            sb.Append(",\"party\":{\"id\":").Append(Json("clovermp-" + party)).Append(",\"size\":[")
                .Append(size).Append(',').Append(SteamSession.MaxPlayers).Append("]}");
        }
        sb.Append('}');
        return sb.ToString();
    }

    // 1,234,567 up to a billion, then 1.23e12 (CloverPit numbers get huge late in a run).
    private static string Short(BigInteger v)
    {
        if (BigInteger.Abs(v) < 1_000_000_000)
        {
            return v.ToString("N0", CultureInfo.InvariantCulture);
        }
        string digits = BigInteger.Abs(v).ToString(CultureInfo.InvariantCulture);
        string sign = v.Sign < 0 ? "-" : "";
        return $"{sign}{digits[0]}.{digits.Substring(1, 2)}e{digits.Length - 1}";
    }

    private static string Short(long v) => Short(new BigInteger(v));

    private static string Json(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ---- IPC (background thread) ----

    private static FileStream _pipe;
    private static DateTime _lastSend = DateTime.MinValue;

    private static void Run()
    {
        DateTime nextConnect = DateTime.MinValue;
        while (!_quit)
        {
            try
            {
                if (_pipe == null)
                {
                    if (DateTime.UtcNow < nextConnect)
                    {
                        Wake.WaitOne(1000);
                        continue;
                    }
                    if (!Connect())
                    {
                        Status = "Discord not running";
                        nextConnect = DateTime.UtcNow.AddSeconds(15);
                        continue;
                    }
                    _lastSent = null;
                }
                string pending = _pending;
                if (pending != null && pending != _lastSent && (DateTime.UtcNow - _lastSend).TotalSeconds >= SendInterval)
                {
                    SendActivity(pending);
                    _lastSent = pending;
                    _lastSend = DateTime.UtcNow;
                }
                // Wake up again in time for a change that had to wait for the send interval.
                Wake.WaitOne(_pending != _lastSent ? 250 : 1000);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("[coop] discord: connection lost (" + e.GetType().Name + ")");
                Status = "reconnecting";
                Close();
                nextConnect = DateTime.UtcNow.AddSeconds(15);
            }
        }
        try
        {
            if (_pipe != null)
            {
                SendActivity("null");
            }
        }
        catch (Exception)
        {
        }
        Close();
    }

    private static string _lastSent;
    private static bool _loggedFirst;

    private static bool Connect()
    {
        for (int i = 0; i < 10; i++)
        {
            SafeFileHandle h = CreateFileW($@"\\.\pipe\discord-ipc-{i}", GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                continue;
            }
            _pipe = new FileStream(h, FileAccess.ReadWrite, 4096, false);
            Write(0, $"{{\"v\":1,\"client_id\":{Json(ClientId)}}}");
            (int op, string body) = Read();
            if (op == 1 && body.Contains("\"READY\""))
            {
                Status = "connected";
                Plugin.Log.LogInfo($"[coop] discord: connected (pipe {i})");
                return true;
            }
            Plugin.Log.LogInfo($"[coop] discord: handshake refused (op {op}): {Trim(body)}");
            Close();
            return false;
        }
        return false;
    }

    private static void SendActivity(string activity)
    {
        int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        string nonce = Guid.NewGuid().ToString("N");
        Write(1, $"{{\"cmd\":\"SET_ACTIVITY\",\"args\":{{\"pid\":{pid},\"activity\":{activity}}},\"nonce\":\"{nonce}\"}}");
        // Read until the answer to our command (Discord may send other frames in between).
        for (int i = 0; i < 5; i++)
        {
            (int op, string body) = Read();
            if (op == 3)
            {
                Write(4, body);   // ping -> pong
                continue;
            }
            if (op == 2)
            {
                throw new IOException("closed by Discord: " + Trim(body));
            }
            if (body.Contains(nonce))
            {
                bool error = body.Contains("\"evt\":\"ERROR\"");
                Status = error ? "error" : "showing";
                if (error)
                {
                    Plugin.Log.LogInfo("[coop] discord: SET_ACTIVITY error: " + Trim(body));
                }
                else if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    Plugin.Log.LogInfo("[coop] discord: status set " + Trim(activity));
                }
                return;
            }
        }
    }

    private static void Write(int op, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 4);
        payload.CopyTo(frame, 8);
        _pipe.Write(frame, 0, frame.Length);
        _pipe.Flush();
    }

    private static (int op, string body) Read()
    {
        byte[] header = ReadExactly(8);
        int op = BitConverter.ToInt32(header, 0);
        int len = BitConverter.ToInt32(header, 4);
        if (len < 0 || len > 1 << 20)
        {
            throw new IOException("bad frame length " + len);
        }
        return (op, Encoding.UTF8.GetString(ReadExactly(len)));
    }

    private static byte[] ReadExactly(int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = _pipe.Read(buf, got, n - got);
            if (r <= 0)
            {
                throw new IOException("pipe closed");
            }
            got += r;
        }
        return buf;
    }

    private static void Close()
    {
        try
        {
            _pipe?.Dispose();
        }
        catch (Exception)
        {
        }
        _pipe = null;
    }

    private static string Trim(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
