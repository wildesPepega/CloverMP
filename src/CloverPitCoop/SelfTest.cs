using System.Collections;
using System.Threading.Tasks;
using Panik;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace CloverPitCoop;

// Self-test, run with --coop-selftest. Two parts, each logs PART PASS/FAIL:
// 1. steam: wait for the game's Steam client, create a private lobby, send a chat
//    line, wait for it to come back, leave.
// 2. spin: a bot clicks through the intro, sits at the slot machine, bets, pulls the
//    lever once and checks that the hooks fired and a spin was consumed.
// Then logs SELFTEST PASS/FAIL and quits.
internal static class SelfTest
{
    private const float SteamTimeout = 60f;
    private const float StepTimeout = 20f;
    private const float RunTimeout = 240f;
    private const string EchoText = "cloverpit-coop-echo";

    internal static int SpinHookCalls;
    internal static bool SawPreparation;
    internal static bool SawGambling;

    private static bool _steamOk;
    private static bool _spinOk;

    public static IEnumerator Run()
    {
        yield return SteamPart();
        yield return SpinPart();
        bool pass = _steamOk && _spinOk;
        Plugin.Log.LogInfo($"[coop] SELFTEST {(pass ? "PASS" : "FAIL")}: steam={_steamOk} spin={_spinOk}");
        Bot.Enabled = false;
        Application.Quit();
    }

    private static void Part(string name, bool pass, string detail)
    {
        Plugin.Log.LogInfo($"[coop] PART {name} {(pass ? "PASS" : "FAIL")}: {detail}");
    }

    private static IEnumerator SteamPart()
    {
        float t = 0f;
        while (!SteamClient.IsValid && t < SteamTimeout)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!SteamClient.IsValid)
        {
            Part("steam", false, "Steam client never became valid");
            yield break;
        }
        Plugin.Log.LogInfo($"[coop] selftest: Steam valid after {t:0.0}s, appid {SteamClient.AppId}");

        Task<Lobby?> create = SteamMatchmaking.CreateLobbyAsync(4);
        t = 0f;
        while (!create.IsCompleted && t < StepTimeout)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!create.IsCompleted || create.IsFaulted || !create.Result.HasValue)
        {
            Part("steam", false, "lobby creation failed or timed out");
            yield break;
        }

        Lobby lobby = create.Result.Value;
        lobby.SetPrivate();
        lobby.SetData("cloverpit-coop", Plugin.Version);
        Plugin.Log.LogInfo($"[coop] selftest: lobby created, members {lobby.MemberCount}, data '{lobby.GetData("cloverpit-coop")}'");

        bool echoed = false;
        void OnChat(Lobby l, Friend from, string text)
        {
            if (l.Id == lobby.Id && text == EchoText)
            {
                echoed = true;
            }
        }
        SteamMatchmaking.OnChatMessage += OnChat;

        bool sent = lobby.SendChatString(EchoText);
        t = 0f;
        while (sent && !echoed && t < StepTimeout)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        SteamMatchmaking.OnChatMessage -= OnChat;
        lobby.Leave();
        Plugin.Log.LogInfo("[coop] selftest: lobby left");

        _steamOk = echoed;
        Part("steam", echoed, echoed ? $"chat echo received after {t:0.0}s" : $"no chat echo (sent={sent})");

        yield return RelayPart();
    }

    // Relay socket: open the host socket, then try a relay connection to our own SteamID and
    // send one line through it. One Steam account may not be allowed to connect to itself;
    // that is reported, not counted as a failure of the mod.
    private static IEnumerator RelayPart()
    {
        Net.SteamLink host = null;
        Net.SteamLink client = null;
        string detail;
        try
        {
            host = Net.SteamLink.Host();
            client = Net.SteamLink.Join(SteamClient.SteamId);
            detail = "socket open";
        }
        catch (System.Exception e)
        {
            Part("relay", false, "socket error: " + e.Message);
            yield break;
        }
        float t = 0f;
        bool sent = false;
        string got = null;
        while (t < 20f && got == null)
        {
            t += Time.unscaledDeltaTime;
            host.Poll();
            client.Poll();
            if (!sent && client.Connected)
            {
                sent = true;
                client.Send("relay-echo");
            }
            if (host.TryReceive(out int from, out string line))
            {
                got = $"{line} from player {from}";
            }
            yield return null;
        }
        Plugin.Log.LogInfo($"[coop] PART relay {(got != null ? "PASS" : "INFO")}: {detail}; " +
            (got != null ? $"self-connection works, received '{got}' after {t:0.0}s"
                : $"no self-connection after {t:0}s (client connected={client.Connected}, error={client.Error ?? "none"})"));
        client.Dispose();
        host.Dispose();
    }

    private static IEnumerator SpinPart()
    {
        yield return ReachGambling();
        if (GameplayMaster.GetGamePhase() != GameplayMaster.GamePhase.gambling)
        {
            Part("spin", false, $"never reached gambling (stage '{ReachStage}', phase {GameplayMaster.GetGamePhase()})");
            yield break;
        }

        // Wait for the machine to be idle, then pull the lever once.
        yield return WaitIdle();
        int spinsBefore = GameplayData.SpinsLeftGet();
        int hooksBefore = SpinHookCalls;
        string coinsBefore = GameplayData.CoinsGet().ToString();
        GameplayMaster.instance.FCall_SlotSpinTry(false);

        float w = 0f;
        while (w < StepTimeout && (SlotMachineScript.IsSpinning() || SlotMachineScript.StateGet() != SlotMachineScript.State.idle))
        {
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        int spinsAfter = GameplayData.SpinsLeftGet();
        string coinsAfter = GameplayData.CoinsGet().ToString();

        bool hookFired = SpinHookCalls > hooksBefore;
        bool spinConsumed = spinsAfter == spinsBefore - 1 || (spinsBefore == 1 && spinsAfter == 0);
        bool settled = w < StepTimeout;
        _spinOk = SawPreparation && SawGambling && hookFired && spinConsumed && settled;
        Part("spin", _spinOk,
            $"phases prep={SawPreparation} gambling={SawGambling}, hook={hookFired}, spins {spinsBefore}->{spinsAfter}, " +
            $"coins {coinsBefore}->{coinsAfter}, settled={settled} after {w:0.0}s");
    }

    internal static string ReachStage = "";

    // A spin is over when the reels stopped and the machine is idle again, or has just
    // switched to "no more spins" / off after the round's last spin. Hashing at that moment
    // gives the same point on every machine; the round-end cutscene starts only later.
    internal static bool SpinSettled()
    {
        SlotMachineScript.State s = SlotMachineScript.StateGet();
        return !SlotMachineScript.IsSpinning() && !PowerupTriggerAnimController.HasAnimations()
            && (s == SlotMachineScript.State.idle || s == SlotMachineScript.State.noMoreCoins || s == SlotMachineScript.State.off);
    }

    internal static IEnumerator WaitSpinSettled()
    {
        yield return null;
        float w = 0f;
        while (!SpinSettled() && w < StepTimeout)
        {
            w += Time.unscaledDeltaTime;
            if (DialogueScript.instance != null && DialogueScript.IsEnabled())
            {
                Bot.TickDialogues();
            }
            yield return null;
        }
    }

    // Waits until the slot machine is idle and no dialogue or menu is open, clicking dialogues away.
    internal static IEnumerator WaitIdle()
    {
        float w = 0f;
        while ((SlotMachineScript.IsSpinning() || SlotMachineScript.StateGet() != SlotMachineScript.State.idle || Bot.DialogueOrMenuOpen()) && w < StepTimeout)
        {
            w += Time.unscaledDeltaTime;
            if (Bot.DialogueOrMenuOpen())
            {
                Bot.TickDialogues();
            }
            yield return null;
        }
    }

    // Bot drives the game from wherever it is (intro scene, dialogue, room) until it sits at the machine.
    internal static IEnumerator ReachGambling()
    {
        Bot.Enabled = true;
        float t = 0f;
        float nextTurnOnTry = 0f;
        string stage = "waiting for preparation";

        // Click through intro scene, intro dialogue and failsafe reminders until we sit at the machine.
        while (t < RunTimeout)
        {
            t += Time.unscaledDeltaTime;
            GameplayMaster.GamePhase phase = GameplayMaster.GetGamePhase();

            if (phase == GameplayMaster.GamePhase.gambling)
            {
                break;
            }
            if (GameplayMaster.instance == null || DialogueScript.instance == null || Bot.DialogueOrMenuOpen())
            {
                if (GameplayMaster.instance != null && ScreenMenuScript.IsEnabled() && stage == "sitting down")
                {
                    Bot.ChooseBet();
                    stage = "bet chosen";
                }
                else
                {
                    Bot.TickDialogues();
                }
                yield return null;
                continue;
            }
            if (phase == GameplayMaster.GamePhase.preparation && t >= nextTurnOnTry)
            {
                nextTurnOnTry = t + 2f;
                Plugin.Log.LogInfo($"[coop] bot: FCall_SlotMachineTurnOnTry (coins {GameplayData.CoinsGet()}, seed {GameplayData.SeedGetAsString()})");
                GameplayMaster.instance.FCall_SlotMachineTurnOnTry();
                stage = "sitting down";
            }
            yield return null;
        }
        ReachStage = stage;
        if (GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.gambling)
        {
            Plugin.Log.LogInfo($"[coop] bot: at the machine after {t:0.0}s, spins left {GameplayData.SpinsLeftGet()}");
        }
    }
}
