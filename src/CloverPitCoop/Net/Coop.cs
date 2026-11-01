using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

internal enum Role { Off, Host, Client }

// Input lockstep with the host as the only authority.
// - Host: every shared action that passes an ActionPatches prefix is checked against the
//   co-op rules, numbered and sent to all clients, then runs.
// - Client: its own actions are not run; they go to the host as requests. The host checks
//   the rules, runs the action itself (which numbers and broadcasts it) and the client runs
//   it when the broadcast comes back. So every machine runs the same actions in the same order.
// - After each action every machine hashes the run state; the client sends its hash back and
//   the host compares.
//
// Rules: one player at the slot machine at a time (from sitting down until the round ends or
// the bet menu is cancelled); only the host may use the phone; everything else is open to all.
//
// Wire format, one line per message:
//   HELLO|<version>                    both ways on connect
//   WELCOME|<player>                   host -> client, the client's player number
//   A|<seq>|<kind>|<arg>|<player>      host -> client, one game action
//   R|<kind>|<arg>                     client -> host, request to run an action
//   X|<kind>|<reason>                  host -> client, request refused
//   H|<seq>|<hash>                     client -> host, state hash after action <seq>
//   END|<lastSeq>                      host -> client, no more actions
//   RESUME|<lastSeq>                   client -> host, back after an interruption: send what came after
//   MO|<player>                        host -> client, the machine changed hands outside an action
//   SU|<player>                        host -> client, the same for the surgery machine
//   NS|<kind>|<dialogue>|<arg>         host -> client, your action fails and changes nothing
//   P|<time>, PO|<time>, PR|<p>|<ms>   ping (may get lost): host asks, client echoes, host shares
//   T|<name>|<data>                    test-only control messages between the bots
internal static class Coop
{
    public const int HostPlayer = 0;
    public const int ClientPlayer = 1;
    private const int NoOne = -1;

    public static Role Role = Role.Off;
    public static ILink Link;
    private static int _myPlayer = ClientPlayer;
    public static int Self => Role == Role.Client ? _myPlayer : HostPlayer;

    // Host side
    public static int LastSentSeq;
    public static int MachineOwner = NoOne;
    // Who is on the phone right now (one at a time). Tracked on every machine.
    public static int PhoneUser = NoOne;
    // Who stands at the surgery machine (one at a time). Tracked on every machine.
    public static int SurgeryUser = NoOne;
    // Host setting (F8): may friends use the phone too?
    public static bool PhoneEveryone;
    private static int _actingPlayer = HostPlayer;
    private static int _lastMappedSeq;
    private static readonly Dictionary<int, string> HostHashes = new Dictionary<int, string>();
    // player -> (seq -> hash)
    private static readonly Dictionary<int, Dictionary<int, string>> ClientHashes = new Dictionary<int, Dictionary<int, string>>();
    public static readonly List<int> Players = new List<int>();
    private static readonly HashSet<int> DesyncShown = new HashSet<int>();

    public static void ClearDesyncNotice(int player)
    {
        if (DesyncShown.Remove(player) && !Automated)
        {
            Show($"{Social.NameOf(player)} is back in sync");
        }
    }
    private static readonly Dictionary<int, string> SentKinds = new Dictionary<int, string>();
    private static readonly Queue<NetAction> Requests = new Queue<NetAction>();
    public static int Matched, Mismatched, RefusedHost, RefusedClient;

    // Client side
    public static bool Replaying;
    public static int Replayed;
    private static int _lastReplayedSeq = -1;
    public static int MutateSkipSeq = -1;
    private static readonly Queue<NetAction> Pending = new Queue<NetAction>();
    private static int _endSeq = -1;
    private static StateTrace _clientTrace;

    // Lets the mod run game code without it counting as a player action.
    public static bool Bypass;

    // Test runs (localhost link): the client's own bot clicks its dialogues away and quits at
    // the end. In real play the player does that.
    public static bool Automated;

    public static bool HelloReceived { get; private set; }
    public static readonly Dictionary<string, string> TestMessages = new Dictionary<string, string>();

    // Short on-screen notice, drawn by Plugin.OnGUI.
    public static string Toast;
    public static float ToastUntil;

    public static void StartHost(int port)
    {
        StartHost(TcpLink.Host(port), $"127.0.0.1:{port}");
    }

    public static void StartHost(ILink link, string where)
    {
        Role = Role.Host;
        Link = link;
        Plugin.Log.LogInfo($"[coop] net: hosting on {where}");
        Plugin.Instance.StartCoroutine(HostRequestLoop());
    }

    public static void StartClient(string host, int port)
    {
        StartClient(TcpLink.Join(host, port), $"{host}:{port}");
    }

    public static void StartClient(ILink link, string where)
    {
        Role = Role.Client;
        Link = link;
        _clientTrace = new StateTrace("client");
        Plugin.Log.LogInfo($"[coop] net: joining {where}");
        Plugin.Instance.StartCoroutine(ClientLoop());
    }

    private static bool _helloSent;

    // Called every frame from Plugin.Update.
    public static void Tick()
    {
        if (Link == null)
        {
            return;
        }
        Link.Poll();
        ApplyTestFixture();
        WatchRunRestart();
        ReleaseStaleLock();
        if (Role == Role.Client && Link.Connected && !_helloSent)
        {
            _helloSent = true;
            Link.Send($"HELLO|{Plugin.Version}|{Social.Encode(Social.MyName)}");
        }
        if (Role == Role.Client)
        {
            ClientConnectionTick();
            if (Link == null)
            {
                return;
            }
        }
        else if (Role == Role.Host)
        {
            HostPingTick();
            while (Link.TryInterrupted(out int lost))
            {
                Interrupted.Add(lost);
                Show($"{Social.NameOf(lost)}: connection lost, waiting …");
            }
            while (Link.TryRejoined(out int back))
            {
                Interrupted.Remove(back);
                Greet(back);
                Show($"{Social.NameOf(back)} is back");
            }
            while (Link.TryJoined(out int joined))
            {
                // Also a player who was gone for longer than the slot is kept.
                Interrupted.Remove(joined);
                if (!Players.Contains(joined))
                {
                    Players.Add(joined);
                }
                JoinSync.HostPlayerJoined(joined);
                Greet(joined);
                Link.SendTo(joined, "MO|" + MachineOwner);
                Link.SendTo(joined, "SU|" + SurgeryUser);
            }
            while (Link.TryLeft(out int left))
            {
                Interrupted.Remove(left);
                Players.Remove(left);
                if (MachineOwner == left)
                {
                    TakeOverMachine(left);
                }
                if (PhoneUser == left)
                {
                    // Puts the phone down for that player on every machine. Stays that player's
                    // until it has run (RunRequest clears it if it could not).
                    Requests.Enqueue(new NetAction(0, "phoneNav", "back", left));
                }
                if (SurgeryUser == left)
                {
                    SurgeryUser = NoOne;
                    Link.Send("SU|" + NoOne);
                }
                JoinSync.AwaitingLoad.Remove(left);
                Show($"{Social.NameOf(left)} left the game");
            }
        }
        while (Link.TryReceive(out int from, out string line))
        {
            Handle(from, line);
        }
        SurgerySync.Tick();
        // Hashing runs the game's save preparation, which must not run while a run is still
        // loading; snapshots are only taken when the host stands in the room (JoinSync.HostStable).
        if (Role == Role.Host)
        {
            JoinSync.HostTick();
            Resync.HostTick();
        }
        else
        {
            JoinSync.ClientTick();
        }
    }

    // Set while the host runs a client's request (between running it and hashing it).
    public static bool HostBusy;

    // The player at the machine is gone for good in the middle of a round. Everyone is in the
    // round's phase and nobody else may pull the lever, so the host continues it.
    private static void TakeOverMachine(int left)
    {
        PhaseSpoof.Suspended = true;
        bool gambling = GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.gambling;
        PhaseSpoof.Suspended = false;
        if (!gambling)
        {
            MachineOwner = NoOne;
            return;
        }
        MachineOwner = HostPlayer;
        Link.Send("MO|" + HostPlayer);
        LocalFreedom.RestoreSlotMenu();
        CameraController.SetPosition(CameraController.PositionKind.Slot_Fixed, instant: false, 1f);
        Show($"{Social.NameOf(left)} is gone: you continue the round at the machine");
    }

    private static void Greet(int player)
    {
        Link.SendTo(player, $"HELLO|{Plugin.Version}|{Social.Encode(Social.MyName)}");
        Link.SendTo(player, "WELCOME|" + player);
        Link.SendTo(player, "PM|" + (PhoneEveryone ? "1" : "0"));
    }

    // ---- connection trouble ----

    // Host: every action sent since the start, for players who missed some while disconnected.
    private const int ActionLogMax = 5000;
    private static readonly List<(int seq, string line)> ActionLog = new List<(int, string)>();

    // Host: players whose connection broke and whose slot is being kept.
    public static readonly HashSet<int> Interrupted = new HashSet<int>();

    // Client: actions arrive in order, whatever the link does. Held until the gap before them is
    // filled; duplicates are dropped.
    private static int _lastReceivedSeq = -1;
    private static readonly SortedDictionary<int, NetAction> HeldActions = new SortedDictionary<int, NetAction>();
    private static float _heldSince;
    private static bool _wasConnected;
    private static bool _down;
    private static bool _phoneModeKnown;

    // Client: requests on their way. One at a time per action, so pressing again while the host
    // has not answered yet (high ping) does not spin or deposit twice.
    private const float RequestTimeout = 45f;
    private const float AnsweredTimeout = 120f;
    private static readonly Dictionary<string, float> Outstanding = new Dictionary<string, float>();
    // The host's answer is here, the action is waiting to be replayed or still running.
    private static readonly HashSet<string> Answered = new HashSet<string>();
    private static bool _waitHintShown;

    public static void ClearRequests()
    {
        Outstanding.Clear();
        Answered.Clear();
    }

    private static List<string> OwnRequestKeys(NetAction a)
    {
        var keys = new List<string>();
        string exact = a.Kind + "|" + a.Arg;
        if (Outstanding.ContainsKey(exact))
        {
            keys.Add(exact);
            return keys;
        }
        foreach (string key in Outstanding.Keys)
        {
            if (key.StartsWith(a.Kind + "|"))
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    // Round trip times in ms, by player; measured by the host, shown in the F8 menu.
    private static readonly Dictionary<int, int> Pings = new Dictionary<int, int>();
    private static readonly Dictionary<int, float> PingSeen = new Dictionary<int, float>();
    private static float _nextPing;

    public static string PingText(int player)
    {
        return Pings.TryGetValue(player, out int ms) && PingSeen.TryGetValue(player, out float seen) && Time.unscaledTime - seen < 5f
            ? ms + " ms"
            : null;
    }

    private static void HostPingTick()
    {
        if (Time.unscaledTime < _nextPing || Players.Count == 0)
        {
            return;
        }
        _nextPing = Time.unscaledTime + 1f;
        Link.SendVolatile("P|" + Time.unscaledTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        foreach (KeyValuePair<int, int> p in Pings)
        {
            if (Time.unscaledTime - PingSeen[p.Key] < 5f)
            {
                Link.SendVolatile($"PR|{p.Key}|{p.Value}");
            }
        }
    }

    // The session is over: leave it and go to the main menu. Whatever is still waiting to be
    // replayed belongs to a run that no longer exists.
    public static void HostGone(string why)
    {
        if (Role != Role.Client)
        {
            return;
        }
        // Before the host's run was loaded this player is still in their own game and stays there.
        bool wasInHostsRun = JoinSync.InCoopRun;
        Plugin.Log.LogInfo($"[coop] net: host gone ({why}), in the host's run: {wasInHostsRun}");
        SteamSession.Leave();
        SteamSession.Status = "The host ended the session";
        Toast = wasInHostsRun ? "The host ended the session – back to the main menu" : "Could not reach the host";
        ToastUntil = Time.unscaledTime + 8f;
    }

    private static void ClientConnectionTick()
    {
        bool up = Link.Connected;
        if (!Automated && !up && (Link.Error != null || SteamSession.HostDroppedFromLobby))
        {
            HostGone(Link.Error ?? "dropped from the lobby and not reachable");
            return;
        }
        if (_wasConnected && !up && Link.Error == null)
        {
            Show("Connection lost – reconnecting …");
            _down = true;
        }
        _wasConnected = up;
        if (_down && !up && Link.Error == null)
        {
            // Stays on screen until it is over either way.
            Toast = "Connection lost – reconnecting …";
            ToastUntil = Time.unscaledTime + 1f;
        }
        if (Link.TryReconnected())
        {
            // Whatever was on its way is gone; the host sends again what this machine missed.
            _down = false;
            ClearRequests();
            PhoneNav.AwaitingHost = false;
            JoinSync.ClientDropPartial();
            Resync.ClientDropPartial();
            Link.Send("RESUME|" + _lastReceivedSeq);
            Plugin.Log.LogInfo($"[coop] net: reconnected, asking for actions after #{_lastReceivedSeq}");
            Show("Reconnected");
        }
        // Before the first run has arrived nothing can be missing: actions just wait for it.
        if (HeldActions.Count > 0 && up && _lastReceivedSeq >= 0 && Time.unscaledTime - _heldSince > 8f)
        {
            _heldSince = Time.unscaledTime;
            Link.Send("RESUME|" + _lastReceivedSeq);
            Plugin.Log.LogInfo($"[coop] net: gap in the actions, asking again after #{_lastReceivedSeq}");
        }
        ExpireRequests();
    }

    private static void ExpireRequests()
    {
        if (Outstanding.Count == 0)
        {
            _waitHintShown = false;
            return;
        }
        bool waiting = false;
        foreach (string key in new List<string>(Outstanding.Keys))
        {
            float age = Time.unscaledTime - Outstanding[key];
            if (Answered.Contains(key))
            {
                if (age > AnsweredTimeout)
                {
                    Outstanding.Remove(key);
                    Answered.Remove(key);
                }
                continue;
            }
            waiting |= age > 2f;
            if (age > RequestTimeout)
            {
                Outstanding.Remove(key);
                Plugin.Log.LogInfo($"[coop] net: no answer to request {key}");
                PhoneNav.AwaitingHost = false;
                PhaseSpoof.Suspended = true;
                bool gambling = GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.gambling;
                PhaseSpoof.Suspended = false;
                if (!gambling && (key.StartsWith("bet|") || key.StartsWith("turnOn|")))
                {
                    CloseSitDownUi();
                }
            }
        }
        // Only a hint on the screen, not a chat line: it can come up on every press with high ping.
        if (waiting && !_waitHintShown)
        {
            Toast = "Waiting for the host …";
            ToastUntil = Time.unscaledTime + 3f;
        }
        _waitHintShown = waiting;
    }

    private static void ReceiveAction(NetAction a)
    {
        if (a.Seq <= _lastReceivedSeq && _lastReceivedSeq >= 0)
        {
            return;
        }
        if (a.Player == Self)
        {
            Answered.UnionWith(OwnRequestKeys(a));
        }
        if (HeldActions.Count == 0)
        {
            _heldSince = Time.unscaledTime;
        }
        HeldActions[a.Seq] = a;
        FlushHeldActions();
    }

    private static void FlushHeldActions()
    {
        while (_lastReceivedSeq >= 0 && HeldActions.TryGetValue(_lastReceivedSeq + 1, out NetAction next))
        {
            HeldActions.Remove(next.Seq);
            _lastReceivedSeq = next.Seq;
            Pending.Enqueue(next);
            _heldSince = Time.unscaledTime;
        }
    }

    // A run from the host contains everything up to its action; later ones follow from there.
    public static void SetActionBase(int seq)
    {
        if (seq > _lastReceivedSeq)
        {
            _lastReceivedSeq = seq;
        }
        foreach (int old in new List<int>(HeldActions.Keys))
        {
            if (old <= _lastReceivedSeq)
            {
                HeldActions.Remove(old);
            }
        }
        FlushHeldActions();
    }

    // Host: a player is back and says what it has. Send the rest, or the whole run if it missed
    // something that cannot be replayed (a run on its way, or actions too old for the log).
    private static void HostResume(int player, int last)
    {
        bool logCovers = last >= 0 && (ActionLog.Count == 0 || ActionLog[0].seq <= last + 1);
        if (JoinSync.AwaitingLoad.Contains(player) || JoinSync.IsWaiting(player) || !logCovers)
        {
            Resync.Forget(player);
            JoinSync.HostPlayerJoined(player);
            Plugin.Log.LogInfo($"[coop] net: player {player} resumes with the whole run");
            return;
        }
        Resync.AfterReconnect(player);
        int sent = 0;
        foreach ((int seq, string line) in ActionLog)
        {
            if (seq > last)
            {
                Link.SendTo(player, line);
                sent++;
            }
        }
        Plugin.Log.LogInfo($"[coop] net: player {player} resumes after #{last}, sent {sent} action(s) again");
    }

    // After a death, a won run or "new run" the game reloads the Game scene and starts a new
    // run with a fresh random seed, separately on every machine. The host then sends its new run
    // to everyone again (like a join); clients stop replaying until it has arrived.
    private static GameplayMaster _sceneSeen;
    // Not "_sceneSeen == null": Unity reports a destroyed object as null, so the old scene's
    // GameplayMaster would look like "never seen" right when a restart has to be noticed.
    private static bool _sceneSeenOnce;

    private static void WatchRunRestart()
    {
        GameplayMaster now = GameplayMaster.instance;
        if (now == null || ReferenceEquals(now, _sceneSeen))
        {
            return;
        }
        bool first = !_sceneSeenOnce;
        _sceneSeenOnce = true;
        _sceneSeen = now;
        if (first || JoinSync.Loading)
        {
            return;
        }
        if (Role == Role.Host)
        {
            // The whole new run replaces any correction still on its way.
            Resync.Reset();
            DesyncShown.Clear();
            foreach (int p in Players)
            {
                JoinSync.HostPlayerJoined(p);
            }
            MachineOwner = NoOne;
            PhoneUser = NoOne;
            SurgeryUser = NoOne;
            SurgerySync.Reset();
            Plugin.Log.LogInfo($"[coop] run restart: the game scene was reloaded, sending the new run to {Players.Count} player(s)");
            Show("New run – sending it to everyone");
        }
        else if (JoinSync.InCoopRun)
        {
            JoinSync.SnapSeq = -1;
            MachineOwner = NoOne;
            PhoneUser = NoOne;
            SurgeryUser = NoOne;
            SurgerySync.Reset();
            Plugin.Log.LogInfo("[coop] run restart: the game scene was reloaded, waiting for the host's new run");
            Show("New run – waiting for the host");
        }
    }

    private static void Handle(int from, string line)
    {
        string[] p = line.Split('|');
        switch (p[0])
        {
            case "HELLO":
                HelloReceived = true;
                string peerName = p.Length > 2 ? Social.Decode(p[2]) : null;
                Plugin.Log.LogInfo($"[coop] net: player {from} says hello, version {p[1]}");
                if (Role == Role.Host)
                {
                    // "joined" is announced once the player stands in the run (JoinSync.HostLoaded).
                    Social.HostLearnedName(from, peerName);
                }
                else if (peerName != null)
                {
                    Social.Names[HostPlayer] = peerName;
                }
                if (p[1] != Plugin.Version)
                {
                    Show($"Warning: different mod version ({p[1]} instead of {Plugin.Version})");
                }
                break;
            case "N":
            case "C":
            case "G":
                Social.Handle(from, p);
                break;
            case "WELCOME" when Role == Role.Client:
                _myPlayer = int.Parse(p[1]);
                Plugin.Log.LogInfo($"[coop] net: I am player {_myPlayer}");
                break;
            case "J" when Role == Role.Client:
                JoinSync.ClientChunk(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), p[4]);
                break;
            case "F" when Role == Role.Client:
                JoinSync.ClientFlags(p[1]);
                break;
            case "L" when Role == Role.Host:
                JoinSync.HostLoaded(from, p[1], p.Length > 2 ? p[2] : null, p.Length > 3 ? int.Parse(p[3]) : -1);
                break;
            case "Z" when Role == Role.Client:
                Resync.ClientChunk(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), p[4], p.Length > 5 ? p[5] : null);
                break;
            case "ZF" when Role == Role.Client:
                Resync.ClientFlags(p[1]);
                break;
            case "ZH" when Role == Role.Host:
                Resync.HostResult(from, int.Parse(p[1]), p[2]);
                break;
            case "D" when Role == Role.Host:
                foreach (string diff in Social.Decode(p[1]).Split('\n'))
                {
                    Plugin.Log.LogInfo($"[coop] diff from player {from}: {diff}");
                }
                break;
            case "K" when Role == Role.Client:
                JoinSync.ClientHostState(int.Parse(p[1]), int.Parse(p[2]), p[3]);
                break;
            case "A" when Role == Role.Client:
                ReceiveAction(new NetAction(int.Parse(p[1]), p[2], p[3], int.Parse(p[4]), p.Length > 5 ? p[5] : null));
                break;
            case "RESUME" when Role == Role.Host:
                HostResume(from, int.Parse(p[1]));
                break;
            case "MO" when Role == Role.Client:
                int newOwner = int.Parse(p[1]);
                if (MachineOwner == Self && newOwner != Self)
                {
                    // This player was gone for too long and the host took over the round.
                    LocalFreedom.LeaveSlotMenu();
                }
                MachineOwner = newOwner;
                break;
            case "SU" when Role == Role.Client:
                SurgeryUser = int.Parse(p[1]);
                break;
            case "NS" when Role == Role.Client:
                // Only this player's action failed, nothing changed in the run.
                foreach (string key in new List<string>(Outstanding.Keys))
                {
                    if (key.StartsWith(p[1] + "|"))
                    {
                        Outstanding.Remove(key);
                        Answered.Remove(key);
                    }
                }
                if (p[1] == "surgery")
                {
                    SurgerySync.Refused();
                }
                if ((p[1] == "equip" || p[1] == "drawer") && p.Length > 3 && FindCharm(p[3]) is PowerupScript inspected)
                {
                    AccessTools.Method(typeof(PowerupScript), "_InspectClose").Invoke(inspected, null);
                }
                ShowFailure(p[2]);
                break;
            case "P" when Role == Role.Client:
                Link.SendVolatile("PO|" + p[1]);
                break;
            case "PO" when Role == Role.Host:
                if (float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sentAt))
                {
                    Pings[from] = Mathf.RoundToInt((Time.unscaledTime - sentAt) * 1000f);
                    PingSeen[from] = Time.unscaledTime;
                }
                break;
            case "PR" when Role == Role.Client:
                Pings[int.Parse(p[1])] = int.Parse(p[2]);
                PingSeen[int.Parse(p[1])] = Time.unscaledTime;
                break;
            case "X" when Role == Role.Client:
                RefusedClient++;
                Show($"Not allowed: {p[2]}");
                Plugin.Log.LogInfo($"[coop] rule: host refused my {p[1]}: {p[2]}");
                if (p[1] == "bet" || p[1] == "turnOn")
                {
                    // Back from the bet menu / screen close-up to the room.
                    CloseSitDownUi();
                }
                if (p[1] == "phoneNav")
                {
                    PhoneNav.AwaitingHost = false;
                }
                if (p[1] == "surgery")
                {
                    SurgerySync.Refused();
                }
                foreach (string key in new List<string>(Outstanding.Keys))
                {
                    if (key.StartsWith(p[1] + "|"))
                    {
                        Outstanding.Remove(key);
                        Answered.Remove(key);
                    }
                }
                break;
            case "PM" when Role == Role.Client:
                bool phoneEveryone = p[1] == "1";
                // Comes again after every reconnect; only worth a line when something changed.
                if (!_phoneModeKnown || phoneEveryone != PhoneEveryone)
                {
                    Show(phoneEveryone ? "Phone: everyone can use it" : "Phone: host only");
                }
                _phoneModeKnown = true;
                PhoneEveryone = phoneEveryone;
                break;
            case "END" when Role == Role.Client:
                _endSeq = int.Parse(p[1]);
                break;
            case "R" when Role == Role.Host:
                Requests.Enqueue(new NetAction(0, p[1], p[2], from));
                break;
            case "H" when Role == Role.Host:
                if (!ClientHashes.TryGetValue(from, out Dictionary<int, string> hashes))
                {
                    hashes = ClientHashes[from] = new Dictionary<int, string>();
                }
                hashes[int.Parse(p[1])] = p[2];
                Compare(int.Parse(p[1]), from);
                break;
            case "T":
                TestMessages[p[1]] = p.Length > 2 ? p[2] : "";
                break;
        }
    }

    public static void Show(string text)
    {
        Toast = text;
        ToastUntil = Time.unscaledTime + 3f;
        if (Role != Role.Off && !Automated)
        {
            Social.SystemLine(UiTheme.Plain(text));
        }
    }

    // ---- rules (host decides) ----

    public static string Refusal(int player, string kind)
    {
        switch (kind)
        {
            // Sitting down only opens the bet menu for that player; the machine is taken with
            // the bet. Two players may have the menu open, the first bet wins.
            case "surgeryOpen":
                if (!PlatformAPI.instance.OwnsDlc1_UnholyFusion())
                {
                    return "the host does not have the DLC";
                }
                if (MachineOwner != NoOne)
                {
                    return "wait until the round is over";
                }
                if (PhoneUser != NoOne)
                {
                    return $"{Social.NameOf(PhoneUser)} is on the phone";
                }
                return SurgeryUser != NoOne && SurgeryUser != player ? $"{Social.NameOf(SurgeryUser)} is at the surgery machine" : null;
            case "surgery":
                return SurgeryUser != player ? "you are not at the surgery machine" : null;
            case "turnOn":
            case "bet":
                if (SurgeryUser != NoOne)
                {
                    return $"{Social.NameOf(SurgeryUser)} is at the surgery machine";
                }
                if (PhoneUser != NoOne)
                {
                    return $"{Social.NameOf(PhoneUser)} is on the phone";
                }
                return MachineOwner != NoOne && MachineOwner != player ? $"the machine is taken ({Social.NameOf(MachineOwner)})" : null;
            case "spin":
            case "redButton":
                return MachineOwner != player ? "you are not at the machine" : null;
            case "phone":
                if (player != HostPlayer && !PhoneEveryone)
                {
                    return "only the host can use the phone";
                }
                if (MachineOwner != NoOne)
                {
                    return "wait until the round is over";
                }
                if (SurgeryUser != NoOne)
                {
                    return $"{Social.NameOf(SurgeryUser)} is at the surgery machine";
                }
                return PhoneUser != NoOne && PhoneUser != player ? $"{Social.NameOf(PhoneUser)} is on the phone" : null;
            case "phoneNav":
                return PhoneUser != player ? "you are not on the phone" : null;
            default:
                return null;
        }
    }

    private static void ApplyRuleEffects(int player, string kind)
    {
        if (kind == "bet")
        {
            MachineOwner = player;
        }
        else if (kind == "phone")
        {
            NotePhonePickup(player);
        }
        else if (kind == "surgeryOpen")
        {
            NoteSurgeryOpen(player);
        }
        else if (kind == "surgeryClose")
        {
            SurgeryUser = NoOne;
        }
    }

    private static void NoteSurgeryOpen(int player)
    {
        SurgeryUser = player;
        // The game otherwise announces the machine again to everyone who did not open it.
        if (Data.game != null)
        {
            Data.game.fusionMachineUsedEver = true;
        }
    }

    // Why this action changes nothing in the run and only concerns the player who tried it:
    // null = it runs. Otherwise the game's dialogue for that player ("" = only the error sound).
    // Such an action is never sent to the others, so they get no message, sound or shake from it.
    private static string WouldFail(string kind, string arg)
    {
        if (GameplayData.Instance == null)
        {
            return null;
        }
        switch (kind)
        {
            case "buy":
                if (!int.TryParse(arg, out int id))
                {
                    return null;
                }
                StoreCapsuleScript capsule = StoreCapsuleScript.GetStoreCapsuleById(id);
                if (capsule == null)
                {
                    return null;
                }
                System.Numerics.BigInteger cost = capsule.GetCapsuleCost();
                if (cost < 0L)
                {
                    return "";
                }
                if (capsule.isRefreshButton)
                {
                    return GameplayData.CoinsGet() < cost ? "" : null;
                }
                if (GameplayData.CloverTicketsGet() < cost)
                {
                    return "";
                }
                PowerupScript forSale = StoreCapsuleScript.storePowerups[id];
                return forSale != null && NoSpaceToEquip(forSale) ? NoSpaceToEquipDialogue : null;
            case "equip":
                PowerupScript toEquip = FindCharm(arg);
                return toEquip != null && NoSpaceToEquip(toEquip) ? NoSpaceToEquipDialogue : null;
            case "drawer":
                for (int i = 0; i < PowerupScript.array_InDrawer.Length; i++)
                {
                    if (PowerupScript.IsDrawerAvailable(i))
                    {
                        return null;
                    }
                }
                return "DIALOGUE_POWERUP_NO_SPACE_IN_DRAWERS";
            case "surgeryOpen":
                return DrawersScript.GetDrawersUnlockedNum() < 4 ? "DIALOGUE_FUSION_MACHINE_LOCKED" : null;
            case "surgery":
                PowerupScript fusion = SurgerySync.FusionCharm(arg);
                return fusion == null || !fusion.IsRecepyReady() || fusion.FusionIsAlreadyCreated()
                    || GameplayData.CoinsGet() < GameplayData.FusionCost || PowerupScript.IsBanned(fusion.identifier, fusion.archetype)
                    ? ""
                    : null;
            default:
                return null;
        }
    }

    private const string NoSpaceToEquipDialogue = "DIALOGUE_POWERUP_NO_SPACE_TO_EQUIP";

    // The same check as PowerupScript.Equip.
    private static bool NoSpaceToEquip(PowerupScript charm)
    {
        if (charm.IsInstantPowerup())
        {
            return false;
        }
        return (charm.category == PowerupScript.Category.skeleton && PowerupScript.list_EquippedSkeleton.Count >= ItemOrganizerScript.SkeletonSlotsN())
            || (charm.category == PowerupScript.Category.normal && PowerupScript.list_EquippedNormal.Count >= GameplayData.MaxEquippablePowerupsGet(considerPowerups: true));
    }

    private static void ShowFailure(string dialogue)
    {
        CameraGame.Shake(1f);
        Sound.Play("SoundMenuError");
        if (!string.IsNullOrEmpty(dialogue))
        {
            DialogueScript.SetDialogue(false, dialogue);
        }
    }

    // A pick-up with a call waiting takes the phone; without one it only closes again by itself.
    private static void NotePhonePickup(int player)
    {
        bool hasCall = !PhoneScript.HasNoDialogue();
        PhoneUser = hasCall ? player : NoOne;
        if (hasCall && player == Self)
        {
            PhoneNav.MarkStarting();
        }
        if (!hasCall && GameplayData.Instance != null)
        {
            // The window sets this only when it closes, 3 s later; the others have it right away.
            GameplayData.Instance._phone_pickedUpOnceLastDeadline = true;
        }
    }

    // Host, F8 menu: who may use the phone.
    public static void SetPhoneEveryone(bool everyone)
    {
        PhoneEveryone = everyone;
        if (Plugin.PhoneEveryoneSetting != null)
        {
            Plugin.PhoneEveryoneSetting.Value = everyone;
        }
        if (Role == Role.Host && Link != null)
        {
            Link.Send("PM|" + (everyone ? "1" : "0"));
        }
    }

    // Safety net: the machine counts as taken only while the shared phase is "gambling".
    // Anything else (a dialogue that interrupted sitting down, a menu closed some other way, a
    // player who left) must not leave a lock behind.
    private static float _ownerWithoutGambling;

    private static void ReleaseStaleLock()
    {
        if (MachineOwner == NoOne)
        {
            _ownerWithoutGambling = 0f;
            return;
        }
        PhaseSpoof.Suspended = true;
        bool gambling = GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.gambling;
        PhaseSpoof.Suspended = false;
        if (gambling || HostBusy)
        {
            _ownerWithoutGambling = 0f;
            return;
        }
        _ownerWithoutGambling += Time.unscaledDeltaTime;
        if (_ownerWithoutGambling > 3f)
        {
            Plugin.Log.LogInfo($"[coop] rule: released machine lock of player {MachineOwner} (not at the machine)");
            MachineOwner = NoOne;
            _ownerWithoutGambling = 0f;
        }
    }

    // The round ended: whoever sat at the machine stands up.
    public static void OnPhaseChanged(GameplayMaster.GamePhase from, GameplayMaster.GamePhase to)
    {
        // Clients track the machine owner too (from replayed turnOn), for the camera.
        if (Role != Role.Off && from == GameplayMaster.GamePhase.gambling && to != GameplayMaster.GamePhase.gambling)
        {
            MachineOwner = NoOne;
        }
    }

    // ---- host ----

    // Gate for the host's own actions and for client requests the host runs.
    private static bool _runningRequest;

    public static bool HostPass(string kind, string arg)
    {
        // Fails on the host alone: the game's own code shows it here, nobody else hears of it.
        if (!_runningRequest && WouldFail(kind, arg) != null)
        {
            return true;
        }
        // The host is not at the machine and does something in the room while someone else
        // plays, or while a surgery runs: run it like a request, between two spins and outside
        // the input code path (where the phase may read as "preparation" on this machine only).
        if (!_runningRequest && LocalFreedom.IsRoomAction(kind) && (LocalFreedom.Active || SurgerySync.Busy))
        {
            Requests.Enqueue(new NetAction(0, kind, arg, HostPlayer));
            Plugin.Log.LogInfo($"[coop] freedom: host {kind} {arg} queued until the machine is idle");
            return false;
        }
        string why = Refusal(_actingPlayer, kind);
        if (why != null)
        {
            if (_actingPlayer == HostPlayer)
            {
                RefusedHost++;
                Show($"Not allowed: {why}");
            }
            Plugin.Log.LogInfo($"[coop] rule: refused {kind} by player {_actingPlayer}: {why}");
            return false;
        }
        ApplyRuleEffects(_actingPlayer, kind);
        LastSentSeq++;
        SentKinds[LastSentSeq] = kind;
        // Phase the host runs it in: room actions may run during someone's round (between spins)
        // or in the room phase, and every machine must run them in the same one.
        PhaseSpoof.Suspended = true;
        string phaseTag = GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.gambling ? "g" : "p";
        PhaseSpoof.Suspended = false;
        string actionLine = $"A|{LastSentSeq}|{kind}|{arg}|{_actingPlayer}|{phaseTag}";
        ActionLog.Add((LastSentSeq, actionLine));
        if (ActionLog.Count > ActionLogMax)
        {
            ActionLog.RemoveAt(0);
        }
        Link.Send(actionLine);
        Plugin.Log.LogInfo($"[coop] net: sent #{LastSentSeq} {kind} {arg} (player {_actingPlayer})");
        if (!Automated && !_runningRequest && _actingPlayer == HostPlayer && IsHashed(kind))
        {
            Plugin.Instance.StartCoroutine(HostSelfHash(LastSentSeq, kind));
        }
        return true;
    }

    // The host's own actions in real play: hash them like everything else, so a difference shows
    // up right there. If the host has already done the next action, that one gets compared instead.
    private static IEnumerator HostSelfHash(int seq, string kind)
    {
        yield return null;
        if (kind == "phoneNav")
        {
            PhoneNav.LastWasHeadless = false;
        }
        yield return Settle(kind);
        if (LastSentSeq != seq)
        {
            yield break;
        }
        HostHashes[seq] = StateHash.Of(StateHash.Json());
        _lastMappedSeq = Mathf.Max(_lastMappedSeq, seq);
        CompareAll(seq);
    }

    // With several charms a spin ends in a row of animations with short gaps, and the state keeps
    // changing through them. Only compare once the state has really stopped changing; otherwise
    // the hash depends on the frame and reports differences that are none.
    public static IEnumerator WaitStable()
    {
        if (GameplayData.Instance == null)
        {
            yield break;
        }
        string last = StateHash.Of(StateHash.Json());
        float total = 0f;
        while (total < 6f)
        {
            float w = 0f;
            while (w < 0.25f)
            {
                w += Time.unscaledDeltaTime;
                yield return null;
            }
            total += w;
            string now = StateHash.Of(StateHash.Json());
            if (now == last && !PowerupTriggerAnimController.HasAnimations() && !SlotMachineScript.IsSpinning())
            {
                yield break;
            }
            last = now;
        }
    }

    private static IEnumerator HostRequestLoop()
    {
        while (true)
        {
            if (Requests.Count == 0)
            {
                yield return null;
                continue;
            }
            NetAction r = Requests.Peek();
            float wait = 0f;
            while (!Ready(r) && wait < 30f)
            {
                wait += Time.unscaledDeltaTime;
                yield return null;
            }
            Requests.Dequeue();
            HostBusy = true;
            yield return RunRequest(r, wait);
            HostBusy = false;
        }
    }

    private static IEnumerator RunRequest(NetAction r, float wait)
    {
        string why = Refusal(r.Player, r.Kind);
        if (why != null || wait >= 30f)
        {
            why ??= "not possible right now";
            Link.SendTo(r.Player, $"X|{r.Kind}|{why}");
            Plugin.Log.LogInfo($"[coop] rule: refused {r.Kind} by player {r.Player}: {why}");
            yield break;
        }
        string failure = WouldFail(r.Kind, r.Arg);
        if (failure != null)
        {
            if (r.Player == Self)
            {
                ShowFailure(failure);
            }
            else
            {
                Link.SendTo(r.Player, $"NS|{r.Kind}|{failure}|{r.Arg}");
            }
            Plugin.Log.LogInfo($"[coop] rule: {r.Kind} {r.Arg} by player {r.Player} fails, only that player is told ({failure})");
            yield break;
        }

        int before = LastSentSeq;
        _actingPlayer = r.Player;
        _runningRequest = true;
        string result = Execute(r);
        _runningRequest = false;
        _actingPlayer = HostPlayer;
        if (r.Kind == "turnOn" && r.Player != Self)
        {
            CloseSitDownUi();
        }
        if (r.Kind == "bet" && r.Player != Self)
        {
            LocalFreedom.LeaveSlotMenu();
        }
        // The phone of a player who is gone must not stay taken, whatever happened above.
        if (r.Kind == "phoneNav" && r.Player != HostPlayer && !Players.Contains(r.Player) && PhoneUser == r.Player)
        {
            PhoneUser = NoOne;
        }
        if (LastSentSeq == before)
        {
            Link.SendTo(r.Player, $"X|{r.Kind}|not run ({result})");
            yield break;
        }
        int seq = LastSentSeq;
        yield return Settle(r.Kind);
        if (IsHashed(r.Kind))
        {
            HostHashes[seq] = StateHash.Of(StateHash.Json());
            _lastMappedSeq = Mathf.Max(_lastMappedSeq, seq);
            CompareAll(seq);
        }
    }

    private static void CompareAll(int seq)
    {
        foreach (int player in ClientHashes.Keys)
        {
            Compare(seq, player);
        }
    }

    // StateTrace calls this after every step the host's bot records; only steps right after a
    // new host action count.
    public static void HostRecorded(string hash)
    {
        if (Role != Role.Host || LastSentSeq <= _lastMappedSeq)
        {
            return;
        }
        _lastMappedSeq = LastSentSeq;
        HostHashes[LastSentSeq] = hash;
        CompareAll(LastSentSeq);
    }

    private static void Compare(int seq, int player)
    {
        if (!HostHashes.TryGetValue(seq, out string h) || !ClientHashes.TryGetValue(player, out Dictionary<int, string> hashes)
            || !hashes.TryGetValue(seq, out string c))
        {
            return;
        }
        // Answers from before the run that player is getting or already has say nothing about it.
        if (JoinSync.AwaitingLoad.Contains(player) || seq <= (JoinSync.SnapshotSeqs.TryGetValue(player, out int snap) ? snap : 0))
        {
            return;
        }
        bool same = h == c;
        if (same) Matched++; else Mismatched++;
        Plugin.Log.LogInfo($"[coop] net: #{seq} {SentKinds[seq]} host {h} player {player} {c} {(same ? "same" : "DESYNC")}");
        if (!same)
        {
            // The host's values win: correct that player at the next quiet moment.
            Resync.Schedule(player);
            if (!Automated && DesyncShown.Add(player))
            {
                Show($"{Social.NameOf(player)} is out of sync, syncing …");
            }
        }
    }

    // Sitting down only opens the bet menu, cancelling only closes it; the state is compared
    // after the bet that follows.
    // Picking up the phone is compared after the phone choice that follows.
    // Standing at the surgery machine changes nothing in the run; the surgery itself does.
    private static bool IsHashed(string kind) => kind != "turnOn" && kind != "cancelSit" && kind != "phone" && kind != "surgeryOpen" && kind != "surgeryClose";

    // After the bot is done: tell the client, wait for its last hashes, log the summary.
    public static IEnumerator HostFinish(string extra)
    {
        Link.Send("END|" + LastSentSeq);
        float t = 0f;
        while (t < 120f && Link.Connected && !AllHashesIn())
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        int expected = 0;
        int missing = 0;
        foreach (int player in Players)
        {
            ClientHashes.TryGetValue(player, out Dictionary<int, string> hashes);
            for (int s = FirstReplayedSeq(player); s <= LastSentSeq; s++)
            {
                if (!IsHashed(SentKinds[s]))
                {
                    continue;
                }
                expected++;
                if (hashes == null || !hashes.ContainsKey(s) || !HostHashes.ContainsKey(s))
                {
                    missing++;
                }
            }
        }
        bool pass = expected > 0 && Mismatched == 0 && missing == 0 && extra == null;
        Plugin.Log.LogInfo($"[coop] NETTEST {(pass ? "PASS" : "FAIL")}: {LastSentSeq} actions, {expected} compared, " +
            $"{Matched} same, {Mismatched} desync, {missing} missing{(extra == null ? "" : ", " + extra)}");
    }

    // A joining player got the run as of its snapshot; it replays only actions after that.
    private static int FirstReplayedSeq(int player)
    {
        return JoinSync.SnapshotSeqs.TryGetValue(player, out int snap) ? snap + 1 : 1;
    }

    private static bool AllHashesIn()
    {
        foreach (int player in Players)
        {
            ClientHashes.TryGetValue(player, out Dictionary<int, string> h);
            for (int s = FirstReplayedSeq(player); s <= LastSentSeq; s++)
            {
                if (IsHashed(SentKinds[s]) && (h == null || !h.ContainsKey(s)))
                {
                    return false;
                }
            }
        }
        return true;
    }

    // ---- client ----

    public static void ClientRequest(string kind, string arg)
    {
        if (!Link.Connected)
        {
            Show("Not connected – reconnecting …");
            PhoneNav.AwaitingHost = false;
            if (kind == "bet" || kind == "turnOn")
            {
                CloseSitDownUi();
            }
            return;
        }
        string key = kind + "|" + arg;
        // The phone has its own guard (PhoneNav.AwaitingHost), and its caller relies on the
        // request going out.
        if (kind != "phoneNav" && Outstanding.ContainsKey(key))
        {
            return;
        }
        Outstanding[key] = Time.unscaledTime;
        Answered.Remove(key);
        Link.Send($"R|{kind}|{arg}");
        Plugin.Log.LogInfo($"[coop] net: requested {kind} {arg}");
    }

    private static IEnumerator ClientLoop()
    {
        Bot.Enabled = Automated;
        while (true)
        {
            if (Link.Error != null && !Link.Connected && Pending.Count == 0)
            {
                Plugin.Log.LogInfo($"[coop] net: link lost ({Link.Error})");
                Show("Lost connection to the host");
                break;
            }
            if (Pending.Count == 0)
            {
                if (Resync.HasPending && !JoinSync.Loading && JoinSync.SnapSeq >= 0
                    && Mathf.Max(_lastReplayedSeq, JoinSync.SnapSeq) >= Resync.PendingSeq)
                {
                    yield return Resync.Apply();
                    continue;
                }
                if (_endSeq >= 0)
                {
                    break;
                }
                if (!ClientTest.Active)
                {
                    ClickDialogues();
                }
                yield return null;
                continue;
            }

            // Nothing is replayed before the host's run is loaded; actions already contained in
            // that run are dropped. A newer run from the host stops replaying past its action.
            if (JoinSync.SnapSeq < 0 || JoinSync.Loading
                || (JoinSync.HasPendingRun && Pending.Peek().Seq > JoinSync.PendingRunSeq))
            {
                ClickDialogues();
                yield return null;
                continue;
            }
            if (Pending.Peek().Seq <= JoinSync.SnapSeq)
            {
                Pending.Dequeue();
                continue;
            }
            // A correction from the host applies right after the action it was made at.
            if (Resync.HasPending && Pending.Peek().Seq > Resync.PendingSeq && _lastReplayedSeq >= Resync.PendingSeq)
            {
                yield return Resync.Apply();
                continue;
            }

            NetAction a = Pending.Peek();
            float wait = 0f;
            bool hinted = false;
            while (!Ready(a) && (!Automated || wait < 60f))
            {
                // Still loading or on the title screen: that is not "stuck", keep waiting.
                if (!StillStarting())
                {
                    wait += Time.unscaledDeltaTime;
                }
                if (!Automated && wait > 5f && !hinted)
                {
                    hinted = true;
                    Show("The others are waiting: close open dialogues or menus");
                }
                ClickDialogues();
                yield return null;
            }
            Pending.Dequeue();
            if (Automated && wait >= 60f)
            {
                Plugin.Log.LogInfo($"[coop] net: #{a.Seq} {a.Kind} not ready after 60s (phase {GameplayMaster.GetGamePhase()}), running anyway");
            }

            Replaying = true;
            // Mutation check (--coop-mutate-skip <seq>): drop one action so the test must report a desync.
            if (a.Kind == "bet")
            {
                MachineOwner = a.Player;
            }
            if (a.Kind == "phone")
            {
                NotePhonePickup(a.Player);
            }
            if (a.Kind == "surgeryOpen")
            {
                NoteSurgeryOpen(a.Player);
            }
            if (a.Kind == "surgeryClose")
            {
                SurgeryUser = NoOne;
            }
            string result = a.Seq == MutateSkipSeq ? "SKIPPED (mutation)" : Execute(a);
            if (a.Kind == "turnOn" && a.Player != Self)
            {
                CloseSitDownUi();
            }
            if (a.Kind == "bet" && a.Player != Self)
            {
                LocalFreedom.LeaveSlotMenu();
            }
            Replaying = false;
            yield return Settle(a.Kind);
            Replayed++;
            _lastReplayedSeq = a.Seq;
            // Own request done: only now may the same action be asked for again.
            if (a.Player == Self)
            {
                foreach (string key in OwnRequestKeys(a))
                {
                    Outstanding.Remove(key);
                    Answered.Remove(key);
                }
            }

            if (!IsHashed(a.Kind))
            {
                Plugin.Log.LogInfo($"[coop] net: replayed #{a.Seq} {a.Kind} {a.Arg} (player {a.Player}) -> {result}");
                continue;
            }
            string hash = Automated ? _clientTrace.Record($"{a.Kind}") : StateHash.Of(StateHash.Json());
            Link.Send($"H|{a.Seq}|{hash}");
            Plugin.Log.LogInfo($"[coop] net: replayed #{a.Seq} {a.Kind} {a.Arg} (player {a.Player}) -> {result}, hash {hash}");
        }
        Plugin.Log.LogInfo($"[coop] net: client done, end seq {_endSeq}, refused {RefusedClient}, saves blocked {ClientSaveBlock.Blocked}");
        if (Automated)
        {
            Plugin.Log.LogInfo($"[coop] join: own save files at end {JoinSync.SaveFilesHash()}");
            yield return new WaitForSecondsRealtime(2f);
            Bot.Enabled = false;
            Application.Quit();
            yield break;
        }
        Stop();
    }

    // Leave co-op and go back to playing alone (the run on this machine stays as it is).
    public static void Stop()
    {
        Plugin.Log.LogInfo("[coop] net: co-op stopped");
        Link?.Dispose();
        Link = null;
        Role = Role.Off;
        MachineOwner = NoOne;
        PhoneUser = NoOne;
        SurgeryUser = NoOne;
        SurgerySync.Reset();
        _sceneSeen = null;
        _sceneSeenOnce = false;
        JoinSync.AwaitingLoad.Clear();
        ActionLog.Clear();
        HeldActions.Clear();
        ClearRequests();
        Interrupted.Clear();
        Pings.Clear();
        PingSeen.Clear();
        _lastReceivedSeq = -1;
        _lastReplayedSeq = -1;
        _wasConnected = false;
        _down = false;
        _phoneModeKnown = false;
        _helloSent = false;
        _endSeq = -1;
        PhoneNav.AwaitingHost = false;
        Replaying = false;
        PhoneEveryone = Plugin.PhoneEveryoneSetting != null && Plugin.PhoneEveryoneSetting.Value;
        Players.Clear();
        Pending.Clear();
        Requests.Clear();
        Social.Reset();
        Resync.Reset();
        Resync.ClientReset();
        Plugin.Instance.StopAllCoroutines();
        // A client played the host's run in memory: go back to the player's own save.
        Plugin.Instance.StartCoroutine(JoinSync.ReloadOwnSave());
    }

    // Dialogues are local UI on every machine; the client clicks its own away like a player would.
    private static void ClickDialogues()
    {
        if (!Automated)
        {
            return;
        }
        bool titleScreen = GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen();
        if (GameplayMaster.instance == null || titleScreen || (DialogueScript.instance != null && DialogueScript.IsEnabled()))
        {
            Bot.TickDialogues();
        }
    }

    // ---- shared ----

    private static bool StillStarting()
    {
        GameplayMaster.GamePhase p = GameplayMaster.GetGamePhase();
        return GameplayMaster.instance == null || p == GameplayMaster.GamePhase.Undefined || p == GameplayMaster.GamePhase.intro
            || (GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen());
    }

    // Test fixture (--coop-test-phonecall): give the phone a call in the loaded run, on every
    // machine at its first preparation phase, before any action is replayed. Same edit everywhere,
    // so lockstep holds; it only makes the phone's choice path reachable from the test save.
    private static bool _fixtureDone;

    public static void ApplyTestFixture()
    {
        // Host only: clients get the host's run (with the fixture already applied) when they join.
        if (_fixtureDone || Role == Role.Client || GameplayData.Instance == null
            || GameplayMaster.GetGamePhase() != GameplayMaster.GamePhase.preparation)
        {
            return;
        }
        _fixtureDone = true;
        if (Plugin.HasArg("--coop-test-phonecall"))
        {
            GameplayData.Instance._phone_abilityAlreadyPickedUp = false;
            GameplayData.Instance._phone_pickedUpOnceLastDeadline = false;
            Plugin.Log.LogInfo("[coop] test fixture: phone call enabled");
        }
        // Enough coins to pay the debt, so the bot reaches the deadline cutscene (debt index up,
        // deadline reward into the interest tray, new rounds).
        if (Plugin.HasArg("--coop-test-rich"))
        {
            GameplayData.CoinsAdd(GameplayData.DebtGet() * 3 + 500, addToStats: false);
            GeneralUiScript.CoinsTextForceUpdate();
            Plugin.Log.LogInfo($"[coop] test fixture: coins {GameplayData.CoinsGet()}, debt {GameplayData.DebtGet()}");
        }
    }

    private static bool Idle()
    {
        return GameplayMaster.instance != null && DialogueScript.instance != null
            && !DialogueScript.IsEnabled() && !PowerupTriggerAnimController.HasAnimations()
            && !ATMScript.DebtClearCutsceneIsPlaying() && !SurgerySync.Busy;
    }

    private static bool Ready(NetAction a)
    {
        // The fixture must be in place before the first replayed action, even when the game
        // reached preparation earlier in this same frame (after Update ran).
        ApplyTestFixture();
        GameplayMaster.GamePhase phase = GameplayMaster.GetGamePhase();
        if (PhoneNav.Starting && a.Kind != "phoneNav")
        {
            return false;
        }
        switch (a.Kind)
        {
            case "spin":
            case "redButton":
                return phase == GameplayMaster.GamePhase.gambling && Idle() && !ScreenMenuScript.IsEnabled()
                    && SlotMachineScript.StateGet() == SlotMachineScript.State.idle && !SlotMachineScript.IsSpinning();
            case "bet":
            case "cancelSit":
                return phase == GameplayMaster.GamePhase.preparation && Idle();
            case "phoneNav":
                // Only the player on the phone has a window to wait for.
                return a.Player != Self || (PhoneNav.UiOpen && PhoneNav.Decision == null);
            case "deposit":
            case "interest":
            case "buy":
            case "sell":
            case "drawer":
            case "equip":
            {
                // Run in the phase the host ran it in. "g": during someone's round, between two
                // spins (spins still left). "p" (or a request the host checks): in the room.
                bool roundRunning = phase == GameplayMaster.GamePhase.gambling && LocalFreedom.MachineIdle()
                    && SlotMachineScript.StateGet() == SlotMachineScript.State.idle && GameplayData.SpinsLeftGet() > 0;
                bool room = phase == GameplayMaster.GamePhase.preparation;
                bool phaseOk = a.HostPhase == "g" ? roundRunning : a.HostPhase == "p" ? room : (room || roundRunning);
                // Host running a friend's deposit: wait out its own ATM press timer.
                bool atmFree = a.Kind != "deposit" || Role != Role.Host || !ATMScript.IsDepositButtonDelayed();
                return phaseOk && atmFree && Idle() && !ScreenMenuScript.IsEnabled();
            }
            default:
                return phase == GameplayMaster.GamePhase.preparation && Idle() && !ScreenMenuScript.IsEnabled();
        }
    }

    private static string Execute(NetAction a)
    {
        GameplayMaster gm = GameplayMaster.instance;
        switch (a.Kind)
        {
            case "turnOn":
                gm.FCall_SlotMachineTurnOnTry();
                return "ok";
            case "bet":
                if (ScreenMenuScript.IsEnabled())
                {
                    ScreenMenuScript.Close(false);
                }
                AccessTools.Method(typeof(GameplayMaster), "_GotoGambling_" + a.Arg).Invoke(gm, null);
                return "ok";
            case "cancelSit":
                if (ScreenMenuScript.IsEnabled())
                {
                    ScreenMenuScript.Close(false);
                }
                AccessTools.Method(typeof(GameplayMaster), "_CancelSlotMachine").Invoke(gm, null);
                return "ok";
            case "spin":
                gm.FCall_SlotSpinTry(a.Arg == "auto");
                return "ok";
            case "surgeryOpen":
                if (a.Player == Self)
                {
                    gm.FCall_SurgeryMachine_Open();
                    return "ok";
                }
                // Someone else stands at it. The host numbers and broadcasts that first.
                return Role == Role.Host && !HostPass("surgeryOpen", "") ? "refused" : "other player";
            case "surgeryClose":
                return Role == Role.Host && !HostPass("surgeryClose", "") ? "refused" : "ok";
            case "surgery":
                if (Role == Role.Host && !HostPass("surgery", a.Arg))
                {
                    return "refused";
                }
                return SurgerySync.Run(a.Player, a.Arg);
            case "redButton":
                RedButtonFx.OthersPress = a.Player != Self;
                try
                {
                    RedButtonScript.instance.Press();
                }
                finally
                {
                    RedButtonFx.OthersPress = false;
                }
                return "ok";
            case "deposit":
                gm.FCall_DepositTry();
                return "ok";
            case "interest":
                gm.FCall_InterestsGetTry();
                return "ok";
            case "phone":
                if (a.Player == Self)
                {
                    gm.FCall_Phone_Pickup();
                    return "ok";
                }
                // Someone else picked up: same game logic here, no window and no camera move.
                // The host numbers and broadcasts the friend's pick-up first.
                if (Role == Role.Host && !HostPass("phone", ""))
                {
                    return "refused";
                }
                PhoneHeadless.Pickup();
                return "headless";
            case "phoneNav":
                if (a.Player == Self)
                {
                    PhoneNav.LastWasHeadless = false;
                    PhoneNav.Decision = a.Arg;
                    return "queued";
                }
                return PhoneHeadless.Nav(a.Arg);
            case "buy":
                gm.FCall_BuyTry(int.Parse(a.Arg));
                return "ok";
            case "sell":
            case "drawer":
            case "equip":
                PowerupScript charm = FindCharm(a.Arg);
                if (charm == null)
                {
                    return "charm not found";
                }
                string method = a.Kind == "sell" ? "_InspectThrowAway" : a.Kind == "drawer" ? "_InspectPutInDrawerTry" : "_InspectEquipTry";
                AccessTools.Method(typeof(PowerupScript), method).Invoke(charm, null);
                return "ok";
            default:
                return "unknown kind";
        }
    }

    // Someone else sat down: on this machine close the bet menu that sitting down opened and
    // put the camera back, without that counting as a cancel.
    private static void CloseSitDownUi()
    {
        Bypass = true;
        if (ScreenMenuScript.IsEnabled())
        {
            ScreenMenuScript.Close(false);
        }
        AccessTools.Method(typeof(GameplayMaster), "_CancelSlotMachine").Invoke(GameplayMaster.instance, null);
        Bypass = false;
    }

    private static PowerupScript FindCharm(string identifier)
    {
        foreach (PowerupScript p in PowerupScript.list_EquippedNormal)
        {
            if (p != null && p.identifier.ToString() == identifier) return p;
        }
        foreach (PowerupScript p in PowerupScript.array_InDrawer)
        {
            if (p != null && p.identifier.ToString() == identifier) return p;
        }
        return null;
    }

    // Same settling on every machine before hashing.
    public static IEnumerator Settle(string kind)
    {
        if (kind == "spin")
        {
            yield return SelfTest.WaitSpinSettled();
            yield break;
        }
        if (kind == "phone")
        {
            // On the machine that picked up: until its phone has rolled the abilities.
            float wait = 0f;
            while (PhoneNav.Starting && wait < 15f)
            {
                wait += Time.unscaledDeltaTime;
                yield return null;
            }
            yield break;
        }
        if (kind == "turnOn" || kind == "cancelSit" || kind == "surgeryOpen" || kind == "surgeryClose")
        {
            yield break;
        }
        if (kind == "phoneNav")
        {
            if (PhoneNav.LastWasHeadless)
            {
                yield return DetTest.Settle();
            }
            else
            {
                yield return PhoneChoiceSettle();
            }
            yield break;
        }
        yield return DetTest.Settle();
    }

    // A phone choice is over when the game consumed it; after a pick or back also wait until
    // the phone is put down, so every machine hashes at the same point.
    public static IEnumerator PhoneChoiceSettle()
    {
        float t = 0f;
        float nextDiag = 2f;
        while (PhoneNav.Decision != null && t < 20f)
        {
            t += Time.unscaledDeltaTime;
            if (t >= nextDiag && PhoneUiScript.instance != null)
            {
                nextDiag += 4f;
                Traverse ui = Traverse.Create(PhoneUiScript.instance);
                Plugin.Log.LogInfo($"[coop] phone diag t={t:0} ui={PhoneNav.UiOpen} state={PhoneScript.StateGet()} " +
                    $"coroutine={ui.Field("mainCoroutine").GetValue() != null} sel={ui.Field("selectionIndex").GetValue()} " +
                    $"noDialogue={PhoneScript.HasNoDialogue()} tick={Panik.Tick.Time:0.000} running={Panik.Tick.IsGameRunning} navCalls={PhoneNav.NavCalls}");
            }
            yield return null;
        }
        if (PhoneNav.LastOutcome == "pick" && Automated)
        {
            yield return PhoneNav.DismissReply();
        }
        if (PhoneNav.LastOutcome != "reroll")
        {
            yield return PhoneNav.WaitClosed();
        }
        yield return DetTest.Settle();
    }

    private readonly struct NetAction
    {
        public readonly int Seq;
        public readonly string Kind;
        public readonly string Arg;
        public readonly int Player;
        public readonly string HostPhase;

        public NetAction(int seq, string kind, string arg, int player, string hostPhase = null)
        {
            Seq = seq;
            Kind = kind;
            Arg = arg;
            Player = player;
            HostPhase = hostPhase;
        }
    }
}
