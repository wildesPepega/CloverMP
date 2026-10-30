using System.Collections;
using UnityEngine;

namespace CloverPitCoop.Net;

// Test bot on the client. When the host's bot hands over a turn (T|TURN), the client plays
// one round through requests: tries the phone (must be refused), deposits, sits down, bets
// and spins until the round is over, then reports back (T|TURNDONE).
internal static class ClientTest
{
    public static bool Active;

    public static void Tick()
    {
        if (Coop.Role == Role.Client && !Active && Coop.TestMessages.Remove("TURN"))
        {
            Plugin.Instance.StartCoroutine(PlayTurn());
        }
    }

    private static IEnumerator PlayTurn()
    {
        Active = true;
        Plugin.Log.LogInfo("[coop] clienttest: my turn");
        yield return WaitStanding();

        int refusedBefore = Coop.RefusedClient;
        GameplayMaster.instance.FCall_Phone_Pickup();
        bool phoneRefused = Coop.RefusedClient > refusedBefore;
        Coop.Link.Send($"T|PHONE|{(phoneRefused ? "refused" : "allowed")}");

        GameplayMaster.instance.FCall_DepositTry();
        yield return WaitReplayOrRefusal();
        yield return WaitStanding();

        // Sit down; a failsafe dialogue may interrupt the first try, like in single player.
        bool menuOpen = false;
        for (int attempt = 0; attempt < 5 && !menuOpen; attempt++)
        {
            yield return WaitStanding();
            GameplayMaster.instance.FCall_SlotMachineTurnOnTry();
            yield return WaitReplayOrRefusal();
            float t = 0f;
            while (t < 5f && !ScreenMenuScript.IsEnabled())
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            menuOpen = ScreenMenuScript.IsEnabled();
        }
        if (!menuOpen)
        {
            Done("bet menu never opened", 0);
            yield break;
        }

        Bot.ChooseBet();
        float g = 0f;
        while (g < 30f && GameplayMaster.GetGamePhase() != GameplayMaster.GamePhase.gambling)
        {
            g += Time.unscaledDeltaTime;
            yield return null;
        }
        if (GameplayMaster.GetGamePhase() != GameplayMaster.GamePhase.gambling)
        {
            Done("never reached gambling", 0);
            yield break;
        }

        int spins = 0;
        while (spins < 15 && GameplayData.SpinsLeftGet() > 0)
        {
            yield return SelfTest.WaitIdle();
            GameplayMaster.instance.FCall_SlotSpinTry(false);
            yield return WaitReplayOrRefusal();
            spins++;
        }
        Done("ok", spins);
    }

    private static void Done(string result, int spins)
    {
        Plugin.Log.LogInfo($"[coop] clienttest: turn done ({result}, {spins} spins)");
        Coop.Link.Send($"T|TURNDONE|{result};{spins}");
        Active = false;
    }

    // Standing in the room with nothing open and no host action still to replay.
    private static IEnumerator WaitStanding()
    {
        float t = 0f;
        while (t < 60f && !(GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.preparation
            && !Bot.DialogueOrMenuOpen() && !PowerupTriggerAnimController.HasAnimations() && !Coop.Replaying))
        {
            t += Time.unscaledDeltaTime;
            if (DialogueScript.instance != null && DialogueScript.IsEnabled())
            {
                Bot.TickDialogues();
            }
            yield return null;
        }
    }

    private static IEnumerator WaitReplayOrRefusal()
    {
        int replayed = Coop.Replayed;
        int refused = Coop.RefusedClient;
        float t = 0f;
        while (t < 30f && Coop.Replayed == replayed && Coop.RefusedClient == refused)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
    }
}
