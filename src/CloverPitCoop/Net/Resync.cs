using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

// Host-authoritative correction. Lockstep keeps the machines equal as long as every action runs
// the same everywhere; when a hash check finds a difference anyway, the host's values win:
//
// 1. Soft correction: at the next quiet moment (room, or between two spins) the host sends its
//    whole GameplayData (money, tickets, deposit, interest and the coins waiting in the interest
//    tray, debt and deadline, rounds and spins, symbol and pattern chances, all random streams,
//    run modifiers, phone, stats) as of action <seq>. The client applies it right after it has
//    replayed action <seq>: JsonUtility.FromJsonOverwrite + GameplayData.Load_Format(), the same
//    conversion the game runs on loading a save, then refreshes the money texts and sends its hash.
// 2. If that hash still differs (live objects such as charms on the shelf or in the store), the
//    host sends its whole run again like on joining and the client reloads it.
//
//   Z|<seq>|<part>|<parts>|<b64>    host -> client, GameplayData JSON as of action <seq>
//   ZF|<flags>                      host -> client, GameplayMaster runtime flags
//   ZH|<seq>|<hash>                 client -> host, hash after applying the correction
internal static class Resync
{
    private const int ChunkSize = 48 * 1024;
    private const float Cooldown = 5f;

    // ---- host ----

    private static readonly HashSet<int> Scheduled = new HashSet<int>();
    private static readonly Dictionary<int, (int seq, string hash)> Sent = new Dictionary<int, (int, string)>();
    private static readonly Dictionary<int, float> LastSent = new Dictionary<int, float>();
    public static int SoftOk, SoftFailed;

    public static void Schedule(int player)
    {
        if (Sent.ContainsKey(player) || JoinSync.IsWaiting(player))
        {
            return;
        }
        if (Scheduled.Add(player))
        {
            Plugin.Log.LogInfo($"[coop] resync: correction for player {player} scheduled");
        }
    }

    // Quiet moment: nothing moving, no request running; also between two spins of a round.
    private static bool HostQuiet()
    {
        if (GameplayMaster.instance == null || GameplayData.Instance == null || Coop.HostBusy)
        {
            return false;
        }
        PhaseSpoof.Suspended = true;
        GameplayMaster.GamePhase p = GameplayMaster.GetGamePhase();
        PhaseSpoof.Suspended = false;
        bool phaseOk = p == GameplayMaster.GamePhase.preparation
            || (p == GameplayMaster.GamePhase.gambling && LocalFreedom.MachineIdle() && SlotMachineScript.StateGet() == SlotMachineScript.State.idle);
        return phaseOk && DialogueScript.instance != null && !DialogueScript.IsEnabled() && !PowerupTriggerAnimController.HasAnimations()
            && !ATMScript.DebtClearCutsceneIsPlaying() && !PhoneUiScript.IsEnabled();
    }

    public static void HostTick()
    {
        if (Scheduled.Count == 0 || !HostQuiet())
        {
            return;
        }
        foreach (int player in new List<int>(Scheduled))
        {
            if (LastSent.TryGetValue(player, out float last) && Time.unscaledTime - last < Cooldown)
            {
                continue;
            }
            Scheduled.Remove(player);
            SendCorrection(player);
        }
    }

    private static void SendCorrection(int player)
    {
        int seq = Coop.LastSentSeq;
        string hash = StateHash.Of(StateHash.Json());   // also runs Save_Prepare
        string json = JsonUtility.ToJson(GameplayData.Instance);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        int parts = (b64.Length + ChunkSize - 1) / ChunkSize;
        PhaseSpoof.Suspended = true;
        string phase = GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.gambling ? "g" : "p";
        PhaseSpoof.Suspended = false;
        for (int i = 0; i < parts; i++)
        {
            int start = i * ChunkSize;
            Coop.Link.SendTo(player, $"Z|{seq}|{i}|{parts}|{b64.Substring(start, Math.Min(ChunkSize, b64.Length - start))}|{phase}");
        }
        Coop.Link.SendTo(player, "ZF|" + JoinSync.ReadFlags());
        Sent[player] = (seq, hash);
        LastSent[player] = Time.unscaledTime;
        Plugin.Log.LogInfo($"[coop] resync: sent correction to player {player} as of action #{seq}, hash {hash}");
    }

    public static void HostResult(int player, int seq, string hash)
    {
        if (!Sent.TryGetValue(player, out (int seq, string hash) s) || s.seq != seq)
        {
            return;
        }
        Sent.Remove(player);
        if (hash == s.hash)
        {
            SoftOk++;
            Plugin.Log.LogInfo($"[coop] resync: player {player} corrected at action #{seq}, hash {hash} matches");
            Coop.ClearDesyncNotice(player);
            return;
        }
        SoftFailed++;
        Plugin.Log.LogInfo($"[coop] resync: player {player} still differs after correction ({hash} vs {s.hash}), sending the whole run");
        JoinSync.HostPlayerJoined(player);
    }

    public static void Reset()
    {
        Scheduled.Clear();
        Sent.Clear();
        LastSent.Clear();
    }

    // ---- client ----

    private static string[] _chunks;
    private static int _seq = -1;
    private static string _json;
    private static string _flags;

    public static bool HasPending => _json != null;
    public static int PendingSeq => _seq;

    private static string _phase = "p";

    public static void ClientChunk(int seq, int part, int parts, string data, string phase)
    {
        _phase = phase ?? "p";
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
        _json = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(_chunks)));
        _seq = seq;
        _chunks = null;
        Plugin.Log.LogInfo($"[coop] resync: received correction as of action #{seq}");
    }

    public static void ClientFlags(string flags)
    {
        _flags = flags;
    }

    // Called by the client loop once every action up to the correction's action has been
    // replayed and settled.
    // The host took its values at a quiet moment, after any cutscene its last action set off
    // (round end, deadline). Applying them earlier would let this machine's own cutscene add the
    // same reward or interest a second time on top. So: same phase as the host, and quiet.
    public static IEnumerator Apply()
    {
        float t = 0f;
        while (t < 180f && !QuietLikeHost())
        {
            t += Time.unscaledDeltaTime;
            if (Coop.Automated && DialogueScript.instance != null && DialogueScript.IsEnabled())
            {
                Bot.TickDialogues();
            }
            yield return null;
        }
        string json = _json;
        int seq = _seq;
        _json = null;
        GameplayData data = GameplayData.Instance;
        if (data == null || json == null)
        {
            yield break;
        }
        JsonUtility.FromJsonOverwrite(json, data);
        data.Load_Format();
        if (_flags != null)
        {
            JoinSync.ApplyFlagString(_flags);
            _flags = null;
        }
        RefreshTexts();
        string hash = StateHash.Of(StateHash.Json());
        Coop.Link.Send($"ZH|{seq}|{hash}");
        Plugin.Log.LogInfo($"[coop] resync: applied host values as of action #{seq}, hash {hash}");
    }

    private static bool QuietLikeHost()
    {
        if (GameplayMaster.instance == null || DetTest.Busy() || SlotMachineScript.IsSpinning())
        {
            return false;
        }
        PhaseSpoof.Suspended = true;
        GameplayMaster.GamePhase p = GameplayMaster.GetGamePhase();
        PhaseSpoof.Suspended = false;
        return _phase == "g"
            ? p == GameplayMaster.GamePhase.gambling && SlotMachineScript.StateGet() == SlotMachineScript.State.idle
            : p == GameplayMaster.GamePhase.preparation;
    }

    private static void RefreshTexts()
    {
        try
        {
            GeneralUiScript.CoinsTextForceUpdate();
            if (ATMScript.instance != null)
            {
                ATMScript.instance.UpdateStrings(forceUpdate: true);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[coop] resync: text refresh failed: " + e.Message);
        }
    }

    public static void ClientReset()
    {
        _chunks = null;
        _json = null;
        _flags = null;
        _seq = -1;
    }
}
