using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CloverPitCoop;

// Determinism test, run with --coop-dettest <label>.
// From whatever save the copy has, the bot plays several rounds. Before each round it
// deposits, buys the cheapest affordable charm, sells one (from round 2) and moves one into
// a drawer if a drawer is unlocked; then it sits down, bets and spins until the round's spins
// are used up. After every action it records the run-state hash into
// BepInEx/coop-trace/<label>. Two runs from the same save must produce identical traces.
internal static class DetTest
{
    private const int MaxRounds = 4;
    private const int MaxSpinsPerRound = 15;
    private const float PhaseTimeout = 120f;

    private static StateTrace _trace;
    private static readonly List<string> Done = new List<string>();

    public static IEnumerator Run(string label)
    {
        _trace = new StateTrace(label);
        Done.Clear();
        Plugin.Log.LogInfo($"[coop] dettest '{label}' started");

        // Over Steam the host role starts only once Steam is up, which can be after this point.
        if (Net.Coop.Role == Net.Role.Host || Plugin.HasArg("--coop-host-steam"))
        {
            float w = 0f;
            while (!Net.Coop.HelloReceived && w < 180f)
            {
                w += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!Net.Coop.HelloReceived)
            {
                Finish(false, "no client connected within 180s");
                yield break;
            }
            // Let the client load the host's run before the bot starts playing.
            w = 0f;
            while (!Net.Coop.TestMessages.ContainsKey("JOINED1") && w < 300f)
            {
                w += Time.unscaledDeltaTime;
                if (GameplayMaster.instance == null || (GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen())
                    || Bot.DialogueOrMenuOpen())
                {
                    Bot.Enabled = true;
                    Bot.TickDialogues();
                }
                yield return null;
            }
            string joined = Net.Coop.TestMessages.TryGetValue("JOINED1", out string j) ? j : "timeout";
            Done.Add("join " + joined);
            if (joined != "same")
            {
                _ruleProblems += $"join: {joined}; ";
            }
        }

        int round = 0;
        bool restarted = false;
        while (round < MaxRounds)
        {
            yield return WaitPreparation();
            if (IsOver() || (GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.cutscene && GameplayMaster.DeathCountdownHasStarted()))
            {
                if (restarted || Net.Coop.Role != Net.Role.Host)
                {
                    break;
                }
                // Death at the deadline: the game starts a new run. Follow it and check that the
                // client gets the host's new run again.
                restarted = true;
                yield return FollowNewRun();
                continue;
            }
            if (GameplayMaster.GetGamePhase() != GameplayMaster.GamePhase.preparation)
            {
                Finish(false, $"round {round + 1}: no preparation phase (phase {GameplayMaster.GetGamePhase()})");
                yield break;
            }
            round++;
            _trace.Record($"r{round}-start");

            if (round == 1)
            {
                yield return UsePhone(round);
            }

            yield return Deposit(round);
            yield return Buy(round);
            if (round >= 2)
            {
                yield return Sell(round);
            }
            yield return PutInDrawer(round);

            if (Net.Coop.Role == Net.Role.Host && round == 2)
            {
                yield return HandTurnToClient(round);
                continue;
            }

            yield return SelfTest.ReachGambling();
            if (GameplayMaster.GetGamePhase() != GameplayMaster.GamePhase.gambling)
            {
                Finish(false, $"round {round}: never reached gambling (stage '{SelfTest.ReachStage}')");
                yield break;
            }
            yield return SelfTest.WaitIdle();
            _trace.Record($"r{round}-bet");

            int spins = 0;
            while (spins < MaxSpinsPerRound && GameplayData.SpinsLeftGet() > 0)
            {
                yield return SelfTest.WaitIdle();
                GameplayMaster.instance.FCall_SlotSpinTry(false);
                yield return SelfTest.WaitSpinSettled();
                spins++;
                _trace.Record($"r{round}-spin{spins}");
            }
            Done.Add($"r{round}:{spins}spins");
        }
        Finish(round > 0, $"{round} rounds, actions [{string.Join(", ", Done)}], phase {GameplayMaster.GetGamePhase()}, " +
            $"seed {GameplayData.SeedGetAsString()}, coins {GameplayData.CoinsGet()}");
    }

    private static bool IsOver()
    {
        GameplayMaster.GamePhase p = GameplayMaster.GetGamePhase();
        return p == GameplayMaster.GamePhase.death || p == GameplayMaster.GamePhase.endingWithoutDeath
            || p == GameplayMaster.GamePhase.closingGame;
    }

    private static IEnumerator FollowNewRun()
    {
        Plugin.Log.LogInfo("[coop] dettest: run over, following the game into a new run");
        Net.Coop.TestMessages.Remove("JOINED1");
        GameplayMaster old = GameplayMaster.instance;
        float t = 0f;
        while (t < 240f && !(GameplayMaster.instance != null && GameplayMaster.instance != old
            && GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.preparation))
        {
            t += Time.unscaledDeltaTime;
            Bot.Enabled = true;
            if (Time.frameCount % 20 == 0)
            {
                Bot.TickDialogues();
            }
            yield return null;
        }
        t = 0f;
        while (t < 240f && !Net.Coop.TestMessages.ContainsKey("JOINED1"))
        {
            t += Time.unscaledDeltaTime;
            if (Bot.DialogueOrMenuOpen())
            {
                Bot.TickDialogues();
            }
            yield return null;
        }
        string again = Net.Coop.TestMessages.TryGetValue("JOINED1", out string j) ? j : "timeout";
        Done.Add("new-run rejoin " + again);
        if (again != "same")
        {
            _ruleProblems += $"new run: {again}; ";
        }
    }

    // Clicks dialogues away until the player stands in the room with nothing open.
    private static IEnumerator WaitPreparation()
    {
        float t = 0f;
        float nextDiag = 5f;
        while (t < PhaseTimeout && !IsOver())
        {
            t += Time.unscaledDeltaTime;
            if (t >= nextDiag)
            {
                nextDiag += 10f;
                Plugin.Log.LogInfo($"[coop] diag t={t:0} scene={Panik.Level.CurrentSceneIndex} phase={GameplayMaster.GetGamePhase()} " +
                    $"running={Panik.Tick.IsGameRunning} timeScale={Time.timeScale} focused={Application.isFocused} " +
                    $"dialogue={(DialogueScript.instance != null && DialogueScript.IsEnabled())} menu={(GameplayMaster.instance != null && ScreenMenuScript.IsEnabled())} " +
                    $"phoneUi={PhoneUiScript.IsEnabled()} mainMenu={MainMenuScript.IsEnabled()}");
            }
            bool busy = GameplayMaster.instance == null || DialogueScript.instance == null || Bot.DialogueOrMenuOpen()
                || PowerupTriggerAnimController.HasAnimations() || ATMScript.DebtClearCutsceneIsPlaying();
            if (!busy && GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.preparation)
            {
                yield return Settle();
                if (!Bot.DialogueOrMenuOpen())
                {
                    yield break;
                }
            }
            // In the intro scene (no GameplayMaster yet) presses skip the logo videos and popups;
            // in the game scene the title screen waits for a press too.
            bool titleScreen = GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen();
            if (GameplayMaster.instance == null || titleScreen || Bot.DialogueOrMenuOpen())
            {
                Bot.Enabled = true;
                Bot.TickDialogues();
            }
            yield return null;
        }
        if (GameplayMaster.instance == null)
        {
            Plugin.Log.LogInfo($"[coop] dettest: still no game scene after {t:0}s (scene {Panik.Level.CurrentSceneIndex})");
        }
    }

    // Lets an action's dialogues, animations and coroutines finish before hashing.
    // Also waits for cutscenes an action sets off by itself, e.g. a deposit that clears the debt
    // starts the deadline cutscene (ATMScript.Update, a frame later): debt index, deadline reward
    // into the interest tray, new rounds. Hashing in the middle of it gives a different point
    // on every machine. In real play nobody clicks for the player: dialogues wait for them.
    internal static IEnumerator Settle()
    {
        for (int i = 0; i < 10; i++)
        {
            yield return null;
        }
        float t = 0f;
        while (t < 120f && Busy())
        {
            t += Time.unscaledDeltaTime;
            if (Bot.DialogueOrMenuOpen())
            {
                Bot.TickDialogues();
            }
            yield return null;
        }
    }

    internal static bool Busy()
    {
        Net.PhaseSpoof.Suspended = true;
        GameplayMaster.GamePhase phase = GameplayMaster.GetGamePhase();
        Net.PhaseSpoof.Suspended = false;
        return Bot.DialogueOrMenuOpen() || PowerupTriggerAnimController.HasAnimations() || ATMScript.DebtClearCutsceneIsPlaying()
            || phase == GameplayMaster.GamePhase.cutscene;
    }

    private static IEnumerator Deposit(int round)
    {
        if (GameplayData.InterestEarnedGet() > 0)
        {
            GameplayMaster.instance.FCall_InterestsGetTry();
            yield return Settle();
            Done.Add($"r{round}:interest");
            _trace.Record($"r{round}-interest");
        }

        if (GameplayData.CoinsGet() < GameplayData.NextDepositAmmountGet())
        {
            Done.Add($"r{round}:deposit-skipped");
            yield break;
        }
        // With --coop-test-rich: keep depositing until the debt is paid (deadline cutscene).
        int presses = Plugin.HasArg("--coop-test-rich") && round == 1 ? 40 : 1;
        int done = 0;
        for (int i = 0; i < presses; i++)
        {
            if (GameplayData.CoinsGet() < GameplayData.NextDepositAmmountGet() || (i > 0 && GameplayData.DepositGet() >= GameplayData.DebtGet()))
            {
                break;
            }
            yield return WaitPreparation();
            GameplayMaster.instance.FCall_DepositTry();
            yield return Settle();
            done++;
            _trace.Record($"r{round}-deposit{i}");
        }
        Done.Add($"r{round}:deposit x{done} (deposit {GameplayData.DepositGet()}, debt {GameplayData.DebtGet()}, interest tray {GameplayData.InterestEarnedGet()})");
    }

    private static IEnumerator Buy(int round)
    {
        StoreCapsuleScript best = null;
        foreach (StoreCapsuleScript c in StoreCapsuleScript.list)
        {
            if (c == null || c.isRefreshButton || c.id < 0 || c.id >= StoreCapsuleScript.storePowerups.Length
                || StoreCapsuleScript.storePowerups[c.id] == null || c.GetCapsuleCost() > GameplayData.CoinsGet())
            {
                continue;
            }
            if (best == null || c.GetCapsuleCost() < best.GetCapsuleCost())
            {
                best = c;
            }
        }
        if (best == null)
        {
            Done.Add($"r{round}:buy-skipped");
            yield break;
        }
        string what = StoreCapsuleScript.storePowerups[best.id].identifier.ToString();
        GameplayMaster.instance.FCall_BuyTry(best.id);
        yield return Settle();
        Done.Add($"r{round}:buy {what}");
        _trace.Record($"r{round}-buy");
    }

    private static IEnumerator Sell(int round)
    {
        PowerupScript charm = FirstEquipped();
        if (charm == null)
        {
            Done.Add($"r{round}:sell-skipped");
            yield break;
        }
        string what = charm.identifier.ToString();
        AccessTools.Method(typeof(PowerupScript), "_InspectThrowAway").Invoke(charm, null);
        yield return Settle();
        Done.Add($"r{round}:sell {what}");
        _trace.Record($"r{round}-sell");
    }

    private static IEnumerator PutInDrawer(int round)
    {
        bool anyUnlocked = false;
        for (int i = 0; i < 4; i++)
        {
            anyUnlocked |= DrawersScript.IsDrawerUnlocked(i) && PowerupScript.array_InDrawer[i] == null;
        }
        PowerupScript charm = FirstEquipped();
        if (!anyUnlocked || charm == null)
        {
            Done.Add($"r{round}:drawer-skipped");
            yield break;
        }
        string what = charm.identifier.ToString();
        AccessTools.Method(typeof(PowerupScript), "_InspectPutInDrawerTry").Invoke(charm, null);
        yield return Settle();
        Done.Add($"r{round}:drawer {what}");
        _trace.Record($"r{round}-drawer");
    }

    // Pick up the phone; if it has a call, reroll once when affordable, then pick the first ability.
    private static IEnumerator UsePhone(int round)
    {
        bool hasCall = !PhoneScript.HasNoDialogue();
        GameplayMaster.instance.FCall_Phone_Pickup();
        float t = 0f;
        while (!Net.PhoneNav.UiOpen && t < 10f)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!Net.PhoneNav.UiOpen)
        {
            Done.Add($"r{round}:phone-did-not-open");
            yield break;
        }
        if (!hasCall)
        {
            yield return Net.PhoneNav.WaitClosed();
            yield return Settle();
            Done.Add($"r{round}:phone-no-call");
            _trace.Record($"r{round}-phone-nocall");
            yield break;
        }

        if (GameplayData.CloverTicketsGet() >= GameplayData.PhoneRerollCostGet())
        {
            Net.PhoneNav.Decision = "1";
            yield return Net.Coop.PhoneChoiceSettle();
            Done.Add($"r{round}:phone-reroll ({Net.PhoneNav.LastOutcome})");
            _trace.Record($"r{round}-phone-reroll");
        }

        string ability = GameplayData.Instance._phone_AbilitiesToPick.Count > 0
            ? GameplayData.Instance._phone_AbilitiesToPick[0].ToString() : "none";
        Net.PhoneNav.Decision = "2";
        yield return Net.Coop.PhoneChoiceSettle();
        Done.Add($"r{round}:phone-pick {ability} ({Net.PhoneNav.LastOutcome})");
        _trace.Record($"r{round}-phone-pick");
    }

    private static PowerupScript FirstEquipped()
    {
        foreach (PowerupScript p in PowerupScript.list_EquippedNormal)
        {
            if (p != null)
            {
                return p;
            }
        }
        return null;
    }

    // Round played by the client: it sits down, bets and spins through requests. While it sits
    // at the machine the host tries to spin, which the machine lock must refuse.
    private static string _ruleProblems;

    private static IEnumerator HandTurnToClient(int round)
    {
        Plugin.Log.LogInfo($"[coop] dettest: round {round} goes to the client");
        Net.Coop.TestMessages.Remove("TURNDONE");
        Net.Coop.Link.Send("T|TURN|");

        float t = 0f;
        while (t < 120f && !(Net.Coop.MachineOwner == Net.Coop.ClientPlayer && GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.gambling))
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (Net.Coop.MachineOwner != Net.Coop.ClientPlayer)
        {
            _ruleProblems += "client never took the machine; ";
        }
        else
        {
            int refused = Net.Coop.RefusedHost;
            GameplayMaster.instance.FCall_SlotSpinTry(false);
            if (Net.Coop.RefusedHost == refused)
            {
                _ruleProblems += "host spin during client turn was NOT refused; ";
            }
            else
            {
                Done.Add($"r{round}:host-spin-refused");
            }

            // Picture of what a player not at the machine sees (machine view, other player's model).
            yield return new WaitForSecondsRealtime(2f);
            string shot = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-spectate.png");
            ScreenCapture.CaptureScreenshot(shot);
            Plugin.Log.LogInfo($"[coop] dettest: screenshot {shot}");

            // The F8 panel and the chat, for a look.
            Net.Social.SystemLine("Test: this is what an info line looks like");
            Net.Social.SendForTest("Hi! This is what the chat looks like.");
            Net.CoopMenu.SetOpen(true);
            yield return new WaitForSecondsRealtime(1f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-menu.png"));
            yield return new WaitForSecondsRealtime(1f);
            Net.CoopMenu.SetOpen(false);
            Net.Social.OpenChatForTest("I am typing");
            yield return new WaitForSecondsRealtime(1f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-chat.png"));
            yield return new WaitForSecondsRealtime(1f);
            Net.Social.CloseChatForTest();

            // Room action while the client plays: must be queued and run between two spins.
            int sentBefore = Net.Coop.LastSentSeq;
            float w2 = 0f;
            while (w2 < 30f && !Net.LocalFreedom.MachineIdle())
            {
                w2 += Time.unscaledDeltaTime;
                yield return null;
            }
            StoreCapsuleScript cheapest = null;
            foreach (StoreCapsuleScript c in StoreCapsuleScript.list)
            {
                if (c != null && !c.isRefreshButton && c.id >= 0 && c.id < StoreCapsuleScript.storePowerups.Length
                    && StoreCapsuleScript.storePowerups[c.id] != null && (cheapest == null || c.GetCapsuleCost() < cheapest.GetCapsuleCost()))
                {
                    cheapest = c;
                }
            }
            if (cheapest != null)
            {
                GameplayMaster.instance.FCall_BuyTry(cheapest.id);
                float w3 = 0f;
                while (w3 < 30f && Net.Coop.LastSentSeq == sentBefore)
                {
                    w3 += Time.unscaledDeltaTime;
                    yield return null;
                }
                bool ran = Net.Coop.LastSentSeq > sentBefore;
                Done.Add($"r{round}:host-buy-during-client-turn {(ran ? "relayed" : "NOT relayed")}");
                if (!ran)
                {
                    _ruleProblems += "host buy during client turn was not relayed; ";
                }
            }
        }

        t = 0f;
        while (t < 300f && !Net.Coop.TestMessages.ContainsKey("TURNDONE"))
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        string turn = Net.Coop.TestMessages.TryGetValue("TURNDONE", out string v) ? v : "timeout";
        Done.Add($"r{round}:client-turn {turn}");
        if (!turn.StartsWith("ok"))
        {
            _ruleProblems += $"client turn: {turn}; ";
        }
        string phone = Net.Coop.TestMessages.TryGetValue("PHONE", out string ph) ? ph : "no report";
        Done.Add($"r{round}:client-phone {phone}");
        if (phone != "refused")
        {
            _ruleProblems += $"client phone {phone}; ";
        }
    }

    private static void Finish(bool ok, string detail)
    {
        Plugin.Log.LogInfo($"[coop] DETTEST {(ok ? "DONE" : "FAIL")}: {detail}");
        Plugin.Instance.StartCoroutine(Quit());
    }

    private static IEnumerator Quit()
    {
        if (Net.Coop.Role == Net.Role.Host)
        {
            yield return Net.Coop.HostFinish(_ruleProblems);
        }
        Bot.Enabled = false;
        Application.Quit();
    }
}
