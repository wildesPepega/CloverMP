using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
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
        if (Role == Role.Host)
        {
            while (Link.TryJoined(out int joined))
            {
                Players.Add(joined);
                JoinSync.HostPlayerJoined(joined);
                Link.SendTo(joined, $"HELLO|{Plugin.Version}|{Social.Encode(Social.MyName)}");
                Link.SendTo(joined, "WELCOME|" + joined);
            }
            while (Link.TryLeft(out int left))
            {
                Players.Remove(left);
                if (MachineOwner == left)
                {
                    MachineOwner = NoOne;
                }
                Show($"{Social.NameOf(left)} left the game");
            }
        }
        while (Link.TryReceive(out int from, out string line))
        {
            Handle(from, line);
        }
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

    // After a death, a won run or "new run" the game reloads the Game scene and starts a new
    // run with a fresh random seed, separately on every machine. The host then sends its new run
    // to everyone again (like a join); clients stop replaying until it has arrived.
    private static GameplayMaster _sceneSeen;

    private static void WatchRunRestart()
    {
        GameplayMaster now = GameplayMaster.instance;
        if (now == null || now == _sceneSeen)
        {
            return;
        }
        bool first = _sceneSeen == null;
        _sceneSeen = now;
        if (first || JoinSync.Loading)
        {
            return;
        }
        if (Role == Role.Host)
        {
            foreach (int p in Players)
            {
                JoinSync.HostPlayerJoined(p);
            }
            MachineOwner = NoOne;
            Plugin.Log.LogInfo($"[coop] run restart: the game scene was reloaded, sending the new run to {Players.Count} player(s)");
            Show("New run – sending it to everyone");
        }
        else if (JoinSync.InCoopRun)
        {
            JoinSync.SnapSeq = -1;
            MachineOwner = NoOne;
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
                JoinSync.HostLoaded(from, p[1], p.Length > 2 ? p[2] : null);
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
            case "K" when Role == Role.Client:
                JoinSync.ClientHostState(int.Parse(p[1]), int.Parse(p[2]), p[3]);
                break;
            case "A" when Role == Role.Client:
                Pending.Enqueue(new NetAction(int.Parse(p[1]), p[2], p[3], int.Parse(p[4]), p.Length > 5 ? p[5] : null));
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
            case "turnOn":
            case "bet":
                return MachineOwner != NoOne && MachineOwner != player ? $"the machine is taken ({Social.NameOf(MachineOwner)})" : null;
            case "spin":
                return MachineOwner != player ? "you are not at the machine" : null;
            case "phone":
                return player != HostPlayer ? "only the host can use the phone" : null;
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
        // The host is not at the machine and does something in the room while someone else
        // plays: run it like a request, between two spins and outside the input code path
        // (where the phase may read as "preparation" on this machine only).
        if (!_runningRequest && LocalFreedom.IsRoomAction(kind) && LocalFreedom.Active)
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
        Link.Send($"A|{LastSentSeq}|{kind}|{arg}|{_actingPlayer}|{phaseTag}");
        Plugin.Log.LogInfo($"[coop] net: sent #{LastSentSeq} {kind} {arg} (player {_actingPlayer})");
        return true;
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
    private static bool IsHashed(string kind) => kind != "turnOn" && kind != "cancelSit" && kind != "phone";

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
            && !ATMScript.DebtClearCutsceneIsPlaying();
    }

    private static bool Ready(NetAction a)
    {
        // The fixture must be in place before the first replayed action, even when the game
        // reached preparation earlier in this same frame (after Update ran).
        ApplyTestFixture();
        GameplayMaster.GamePhase phase = GameplayMaster.GetGamePhase();
        switch (a.Kind)
        {
            case "spin":
                return phase == GameplayMaster.GamePhase.gambling && Idle() && !ScreenMenuScript.IsEnabled()
                    && SlotMachineScript.StateGet() == SlotMachineScript.State.idle && !SlotMachineScript.IsSpinning();
            case "bet":
            case "cancelSit":
                return phase == GameplayMaster.GamePhase.preparation && Idle();
            case "phoneNav":
                return PhoneNav.UiOpen && PhoneNav.Decision == null;
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
            case "deposit":
                gm.FCall_DepositTry();
                return "ok";
            case "interest":
                gm.FCall_InterestsGetTry();
                return "ok";
            case "phone":
                gm.FCall_Phone_Pickup();
                return "ok";
            case "phoneNav":
                PhoneNav.Decision = a.Arg;
                return "queued";
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
        if (kind == "turnOn" || kind == "cancelSit" || kind == "phone")
        {
            yield break;
        }
        if (kind == "phoneNav")
        {
            yield return PhoneChoiceSettle();
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
        if (PhoneNav.LastOutcome == "pick")
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
