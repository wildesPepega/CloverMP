using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

// Join sync. Lockstep needs every machine to run the same run, but every friend has their own
// save. So when a player joins, the host sends its whole GameData (run + meta progress such as
// unlocked charms and drawers, as JSON exactly like its save file) and a few runtime flags of
// GameplayMaster that are not in the save. The client loads that in memory only, restarts the
// game scene on it, and then replays the host's actions from the snapshot's action number on.
// The client's own save file is never written while in co-op, and is loaded again on leaving.
//
//   J|<snapSeq>|<part>|<parts>|<base64 chunk>   host -> joining client, GameData JSON
//   F|<name=value;...>                          host -> joining client, runtime flags
//   L|<hash>|<dlc>|<snapSeq>                    client -> host, hash after loading
internal static class JoinSync
{
    private const int ChunkSize = 48 * 1024;

    // GameplayMaster runtime fields that change game logic but are not saved.
    private static readonly string[] StaticFlags =
    {
        "_failsafeTriggered_OverDeposit", "_failsafeTriggered_NeverDeposited", "_failsafeTriggered_NeverEquippedACharm",
        "unlockPowerupFirstTimeDialogueBooked", "_cranePackDialogueShown",
    };
    private static readonly string[] InstanceFlags =
    {
        "interestsAwarded", "_1RoundLeftWarned", "dialogueJustInterruptedTurnOn",
        "_theDealIsOff_BookedDialogue", "_theDealIsOff_ByCoins",
        "unlockCheckPerformed_PossessedPhone", "unlockCheckPerformed_EvilDeal", "unlockCheckPerformed_Wallet",
    };

    // ---- host ----

    private static readonly Queue<int> Waiting = new Queue<int>();
    public static readonly Dictionary<int, string> SnapshotHashes = new Dictionary<int, string>();
    // Last action already contained in the run a player got; that player replays only later ones.
    public static readonly Dictionary<int, int> SnapshotSeqs = new Dictionary<int, int>();

    public static void HostPlayerJoined(int player)
    {
        if (!Waiting.Contains(player))
        {
            Waiting.Enqueue(player);
        }
    }

    public static bool IsWaiting(int player) => Waiting.Contains(player);

    // Players who were sent a whole run and have not reported back yet.
    public static readonly HashSet<int> AwaitingLoad = new HashSet<int>();

    // Players who got the run and stand in the room (for the F8 list and the join notice).
    public static readonly HashSet<int> InRoom = new HashSet<int>();

    // A snapshot is only consistent while nothing is moving: standing in the room, no dialogue,
    // no animation, no spin, no request being run, phone down.
    public static bool HostStable()
    {
        return GameplayMaster.instance != null && GameplayData.Instance != null
            && GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.preparation
            && DialogueScript.instance != null && !DialogueScript.IsEnabled()
            && !ScreenMenuScript.IsEnabled() && !PowerupTriggerAnimController.HasAnimations()
            && !ATMScript.DebtClearCutsceneIsPlaying() && !SlotMachineScript.IsSpinning()
            && !PhoneUiScript.IsEnabled() && !Coop.HostBusy && !SurgerySync.Busy && !SurgeryMachine.IsEnabled()
            && !(GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen());
    }

    private const float LoadTimeout = 180f;
    private static readonly Dictionary<int, float> RunSentAt = new Dictionary<int, float>();

    public static void HostTick()
    {
        // No answer to a whole run for this long: the run or the answer got lost, send it again.
        if (AwaitingLoad.Count > 0)
        {
            foreach (int p in new List<int>(AwaitingLoad))
            {
                if (RunSentAt.TryGetValue(p, out float at) && Time.unscaledTime - at > LoadTimeout)
                {
                    RunSentAt[p] = Time.unscaledTime;
                    Plugin.Log.LogInfo($"[coop] join: no answer from player {p}, sending the run again");
                    HostPlayerJoined(p);
                }
            }
        }
        if (Waiting.Count == 0 || !HostStable())
        {
            return;
        }
        int player = Waiting.Dequeue();
        int snapSeq = Coop.LastSentSeq;
        Data.game.Saving_Prepare(saveGameplayData: true, Data.GameSavingReason.debug);
        string json = PlatformDataMaster.ToJson(Data.game);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        int parts = (b64.Length + ChunkSize - 1) / ChunkSize;
        for (int i = 0; i < parts; i++)
        {
            int start = i * ChunkSize;
            Coop.Link.SendTo(player, $"J|{snapSeq}|{i}|{parts}|{b64.Substring(start, Math.Min(ChunkSize, b64.Length - start))}");
        }
        Coop.Link.SendTo(player, "F|" + ReadFlags());
        string stateJson = StateHash.Json();
        SnapshotHashes[player] = StateHash.Of(stateJson);
        File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-join-host.json"), stateJson);
        // The hashed state itself, so the client can log which fields differ after loading.
        string sb64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(stateJson));
        int sparts = (sb64.Length + ChunkSize - 1) / ChunkSize;
        for (int i = 0; i < sparts; i++)
        {
            int start = i * ChunkSize;
            Coop.Link.SendTo(player, $"K|{i}|{sparts}|{sb64.Substring(start, Math.Min(ChunkSize, sb64.Length - start))}");
        }
        SnapshotSeqs[player] = snapSeq;
        AwaitingLoad.Add(player);
        RunSentAt[player] = Time.unscaledTime;
        Resync.Forget(player);
        Plugin.Log.LogInfo($"[coop] join: sent run to player {player}: {json.Length} chars in {parts} parts, at action #{snapSeq}, hash {SnapshotHashes[player]}");
    }

    public static void HostLoaded(int player, string hash, string clientDlc, int loadedSeq)
    {
        // Answer to an older run; a newer one is on its way to that player.
        if (loadedSeq >= 0 && SnapshotSeqs.TryGetValue(player, out int newest) && loadedSeq != newest)
        {
            Plugin.Log.LogInfo($"[coop] join: player {player} loaded the run of action #{loadedSeq}, newest is #{newest}");
            return;
        }
        SnapshotHashes.TryGetValue(player, out string expected);
        bool same = hash == expected;
        AwaitingLoad.Remove(player);
        Plugin.Log.LogInfo($"[coop] join: player {player} loaded the run, hash {hash}, host snapshot {expected} -> {(same ? "same" : "DIFFERENT")}");
        if (same)
        {
            Coop.ClearDesyncNotice(player);
        }
        string hostDlc = OwnsDlc();
        if (clientDlc != null && clientDlc != hostDlc)
        {
            Plugin.Log.LogInfo($"[coop] join: DLC differs: host {hostDlc}, player {player} {clientDlc}");
            Coop.Show($"Warning: {Social.NameOf(player)} and the host do not both have the DLC – this will go out of sync");
        }
        bool first = InRoom.Add(player);
        Coop.Show(!same ? $"{Social.NameOf(player)}: run differs, syncing …"
            : first ? $"{Social.NameOf(player)} joined" : $"{Social.NameOf(player)} is in the new run");
        if (!same)
        {
            Resync.Schedule(player);
        }
        if (Coop.Automated)
        {
            Coop.TestMessages["JOINED" + player] = same ? "same" : "different";
        }
    }

    // The game branches on DLC ownership (e.g. charms equipped at a new run), so a mixed group
    // cannot stay in sync.
    public static string OwnsDlc()
    {
        return PlatformAPI.instance != null && PlatformAPI.instance.OwnsDlc1_UnholyFusion() ? "dlc" : "base";
    }

    private static string[] _stateChunks;
    private static string _hostStateJson;

    public static void ClientHostState(int part, int parts, string data)
    {
        if (_stateChunks == null || _stateChunks.Length != parts)
        {
            _stateChunks = new string[parts];
        }
        _stateChunks[part] = data;
        foreach (string c in _stateChunks)
        {
            if (c == null)
            {
                return;
            }
        }
        _hostStateJson = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(_stateChunks)));
        _stateChunks = null;
    }

    // Logs the first lines where this machine's loaded state differs from the host's.
    private static int LogDiff(string mine)
    {
        if (_hostStateJson == null)
        {
            Plugin.Log.LogInfo("[coop] join: no host state received for comparison");
            return -1;
        }
        return ReportDiff("join", _hostStateJson, mine);
    }

    // Logs where this machine's state differs from the host's and tells the host too, so the
    // fields also end up in the host's log.
    public static int ReportDiff(string what, string host, string mine)
    {
        string[] h = host.Split('\n');
        string[] c = mine.Split('\n');
        var lines = new List<string>();
        int diffs = 0;
        for (int i = 0; i < Math.Max(h.Length, c.Length); i++)
        {
            string hl = i < h.Length ? h[i].Trim() : "<none>";
            string cl = i < c.Length ? c[i].Trim() : "<none>";
            if (hl == cl)
            {
                continue;
            }
            diffs++;
            if (diffs <= 20)
            {
                // The field name is on this line or, for array items, on an earlier line.
                string field = "";
                for (int j = Math.Min(i, h.Length - 1); j >= 0; j--)
                {
                    if (h[j].Contains("\":"))
                    {
                        field = h[j].Trim();
                        break;
                    }
                }
                lines.Add($"{what} line {i} ({Cut(field)}): host {Cut(hl)} | mine {Cut(cl)}");
            }
        }
        lines.Add($"{what}: {diffs} differing lines");
        foreach (string line in lines)
        {
            Plugin.Log.LogInfo("[coop] diff " + line);
        }
        if (diffs > 0 && Coop.Link != null)
        {
            Coop.Link.Send("D|" + Social.Encode(string.Join("\n", lines)));
        }
        return diffs;
    }

    private static string Cut(string s) => s.Length > 90 ? s.Substring(0, 90) + "…" : s;

    public static string ReadFlags()
    {
        var sb = new StringBuilder();
        foreach (string f in StaticFlags)
        {
            sb.Append(f).Append('=').Append(Traverse.Create(typeof(GameplayMaster)).Field(f).GetValue()).Append(';');
        }
        foreach (string f in InstanceFlags)
        {
            sb.Append(f).Append('=').Append(Traverse.Create(GameplayMaster.instance).Field(f).GetValue()).Append(';');
        }
        return sb.ToString();
    }

    // ---- client ----

    public static bool Loading;      // between receiving the run and standing in the room on it
    public static bool InCoopRun;    // the loaded run is the host's; never save it
    public static int SnapSeq = -1;
    private static string[] _chunks;
    private static string _flags;
    private static string _saveHashAtJoin;

    public static void ClientChunk(int snapSeq, int part, int parts, string data)
    {
        if (_chunks == null || _chunks.Length != parts)
        {
            _chunks = new string[parts];
        }
        _chunks[part] = data;
        foreach (string c in _chunks)
        {
            if (c == null)
            {
                return;
            }
        }
        _pendingJson = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(_chunks)));
        _pendingSeq = snapSeq;
        _chunks = null;
        Coop.SetActionBase(snapSeq);
        Plugin.Log.LogInfo($"[coop] join: received host run ({_pendingJson.Length} chars, action #{snapSeq})");
    }

    // The connection broke in the middle of a run; the host sends all of it again.
    public static void ClientDropPartial()
    {
        _chunks = null;
        _stateChunks = null;
    }

    private static string _pendingJson;
    private static int _pendingSeq;

    // A whole run from the host is waiting to be loaded; the client replays nothing after it.
    public static bool HasPendingRun => _pendingJson != null || Loading;
    public static int PendingRunSeq => _pendingSeq;

    // The host's run replaces this machine's run only once this machine's own game has finished
    // starting and nothing is moving, so the game's own loading sequence is never cut short.
    public static void ClientTick()
    {
        if (_pendingJson == null || Loading)
        {
            return;
        }
        GameplayMaster.GamePhase phase = GameplayMaster.GetGamePhase();
        bool ownGameReady = GameplayMaster.instance != null
            && (phase == GameplayMaster.GamePhase.preparation || phase == GameplayMaster.GamePhase.gambling || phase == GameplayMaster.GamePhase.cutscene)
            && !SlotMachineScript.IsSpinning() && !PowerupTriggerAnimController.HasAnimations()
            && !(GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen());
        if (InCoopRun && !ownGameReady)
        {
            return;
        }
        // First join: wait until this machine stands in the room on its own run.
        ownGameReady &= InCoopRun || phase == GameplayMaster.GamePhase.preparation;
        if (!ownGameReady)
        {
            return;
        }
        string json = _pendingJson;
        _pendingJson = null;
        Load(_pendingSeq, json);
    }

    public static void ClientFlags(string flags)
    {
        _flags = flags;
    }

    private static void Load(int snapSeq, string json)
    {
        Data.GameData data = PlatformDataMaster.FromJson<Data.GameData>(json, out bool error);
        if (error || data == null)
        {
            Plugin.Log.LogError("[coop] join: could not read the host's run");
            Coop.Show("Could not load the host's run");
            return;
        }
        if (!InCoopRun)
        {
            _saveHashAtJoin = SaveFilesHash();
            Plugin.Log.LogInfo($"[coop] join: own save files at join {_saveHashAtJoin}");
        }
        SnapSeq = snapSeq;
        Resync.ClientReset();
        Coop.ClearRequests();
        InCoopRun = true;
        Loading = true;
        data.Loading_Prepare();
        Data.GameData.inst = data;
        CaptureRng(data.gameplayData);
        Plugin.Log.LogInfo($"[coop] join: loaded host run ({json.Length} chars, action #{snapSeq}), restarting the game scene");
        Coop.Show("Loading the host's run …");
        Plugin.Instance.StartCoroutine(AfterReload(GameplayMaster.instance, snapSeq));
        Level.GoTo(Level.SceneIndex.Game, asyncLoad: false);
    }

    private static IEnumerator AfterReload(GameplayMaster oldScene, int snapSeq)
    {
        // Wait for the new scene (a new GameplayMaster) to stand in the room. Dialogues of the
        // intro are clicked like a player would (they only show text).
        yield return null;
        float t = 0f;
        while (t < 120f && !(GameplayMaster.instance != null && GameplayMaster.instance != oldScene
            && GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.preparation
            && DialogueScript.instance != null && !DialogueScript.IsEnabled() && !PowerupTriggerAnimController.HasAnimations()))
        {
            if (DialogueScript.instance != null && DialogueScript.IsEnabled())
            {
                Inject.Press(Controls.InputAction.menuSelect);
            }
            yield return new WaitForSecondsRealtime(0.25f);
            t += 0.25f;
        }
        ApplyFlags();
        RestoreRng(GameplayData.Instance);
        Loading = false;
        string loadedJson = StateHash.Json();
        string hash = StateHash.Of(loadedJson);
        File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-join-client.json"), loadedJson);
        LogDiff(loadedJson);
        Coop.Link.Send($"L|{hash}|{OwnsDlc()}|{snapSeq}");
        Plugin.Log.LogInfo($"[coop] join: standing in the host's run after {t:0}s, hash {hash}");
        Coop.Show("You are in the host's run");
    }

    // Starting the game scene on a loaded run draws from some random streams again
    // (observed: rngSymbolsMod +150, rngGarbage +15, modifierChanceFloats re-rolled). The host's
    // snapshot already contains its own scene start, so the client puts the host's values back.
    private static readonly Dictionary<string, string> RngStates = new Dictionary<string, string>();
    private static float[] _modifierChanceFloats;

    private static void CaptureRng(GameplayData d)
    {
        RngStates.Clear();
        foreach (System.Reflection.FieldInfo f in typeof(GameplayData).GetFields())
        {
            if (f.FieldType == typeof(Rng) && f.GetValue(d) is Rng r)
            {
                RngStates[f.Name] = JsonUtility.ToJson(r);
            }
        }
        _modifierChanceFloats = (Traverse.Create(d).Field("modifierChanceFloats").GetValue() as float[])?.Clone() as float[];
    }

    private static void RestoreRng(GameplayData d)
    {
        if (d == null)
        {
            return;
        }
        foreach (KeyValuePair<string, string> s in RngStates)
        {
            if (typeof(GameplayData).GetField(s.Key)?.GetValue(d) is Rng r)
            {
                JsonUtility.FromJsonOverwrite(s.Value, r);
            }
        }
        if (_modifierChanceFloats != null)
        {
            Traverse.Create(d).Field("modifierChanceFloats").SetValue(_modifierChanceFloats.Clone());
        }
    }

    private static void ApplyFlags()
    {
        if (_flags == null)
        {
            return;
        }
        ApplyFlagString(_flags);
    }

    public static void ApplyFlagString(string flags)
    {
        foreach (string pair in flags.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            string name = pair.Substring(0, eq);
            bool value = pair.Substring(eq + 1) == "True";
            Traverse field = Array.IndexOf(StaticFlags, name) >= 0
                ? Traverse.Create(typeof(GameplayMaster)).Field(name)
                : Traverse.Create(GameplayMaster.instance).Field(name);
            field.SetValue(value);
        }
    }

    // Back to the player's own save after leaving co-op.
    public static IEnumerator ReloadOwnSave()
    {
        if (!InCoopRun)
        {
            yield break;
        }
        InCoopRun = false;
        string now = SaveFilesHash();
        Plugin.Log.LogInfo($"[coop] join: own save files {(now == _saveHashAtJoin ? "unchanged" : "CHANGED")} during co-op ({_saveHashAtJoin} -> {now})");
        yield return LoadSaveAndShowTitle();
    }

    // F8 "Main menu" of a host or a player alone. Saves like the game does when its pause menu
    // opens: in the room yes, during a round no, so a round cannot be undone by going to the menu.
    public static IEnumerator ToMainMenu()
    {
        PhaseSpoof.Suspended = true;
        bool inRoom = GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.preparation;
        PhaseSpoof.Suspended = false;
        if (inRoom)
        {
            UniTask<bool> save = Data.SaveGame(Data.GameSavingReason.mainMenuOpened_NotDuringSlotMachine);
            while (save.Status == UniTaskStatus.Pending)
            {
                yield return null;
            }
        }
        yield return LoadSaveAndShowTitle();
    }

    // The game shows its title screen and the Continue / New run menu when the game scene starts,
    // but skips them after a death in this session unless it comes from the victory screen. That
    // flag has no other effect (GeneralUiScript.IntroAndTitleScreen_Coroutine reads it).
    private static IEnumerator LoadSaveAndShowTitle()
    {
        UniTask<bool> load = Data.LoadGame(Data.GameDataIndex, forceLoadSameFile: true);
        while (load.Status == UniTaskStatus.Pending)
        {
            yield return null;
        }
        Loading = false;
        SnapSeq = -1;
        InputBlock.Set(false);
        Sound.StopAll();
        GeneralUiScript.ComingFromVictoryFlag_Set(true);
        Level.GoTo(Level.SceneIndex.Game, asyncLoad: false);
    }

    public static string SaveFilesHash()
    {
        string dir = Path.Combine(BepInEx.Paths.GameRootPath, "SaveData");
        if (!Directory.Exists(dir))
        {
            return "none";
        }
        var files = new List<string>(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
        files.Sort(StringComparer.Ordinal);
        using SHA256 sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (string f in files)
        {
            sb.Append(f.Substring(dir.Length)).Append(':').Append(Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f)))).Append(';');
        }
        return StateHash.Of(sb.ToString());
    }
}

// While the client plays the host's run, its own save file stays untouched.
[HarmonyPatch(typeof(Data), nameof(Data.SaveGame))]
internal static class ClientSaveBlock
{
    public static int Blocked;

    private static bool Prefix(ref UniTask<bool> __result)
    {
        if (Coop.Role == Role.Client || JoinSync.InCoopRun)
        {
            Blocked++;
            __result = UniTask.FromResult(false);
            return false;
        }
        return true;
    }
}

// The reload after joining skips the title screen and the Continue / New run menu, like the
// game's own quick restart after a death does.
[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.HasDiedOnce))]
internal static class JoinSkipTitle
{
    private static void Postfix(ref bool __result)
    {
        if (JoinSync.Loading)
        {
            __result = true;
        }
    }
}
