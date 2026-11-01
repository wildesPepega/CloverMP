using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

// The surgery machine (DLC). One player at a time stands at it, browsing is local to that player.
// The surgery itself changes the run (coins, cost, charms eaten, fusion charm made), so it is a
// relayed action and the game's own routine runs on every machine in the same order: the others
// see and hear it in the room, without their camera moving, flashes, shakes or dialogues.
//
// A player without the DLC has no machine in the game and gets none here: the mod does not unlock
// paid content. A mixed group stays out of sync, which the mod warns about on joining.
internal static class SurgerySync
{
    public enum Step { Proceed, Wait, Cancel }

    private const float RequestTimeout = 50f;
    private const float ForeignTimeout = 30f;

    private static bool _foreign;
    private static bool _foreignStarting;
    private static float _foreignSince;
    private static SurgeryMachine _foreignOn;

    private static bool _waiting;
    private static bool _goAhead;
    private static bool _refused;
    private static float _waitSince;

    private static bool _sawOpen;
    private static float _userSince;

    // This machine shows a surgery someone else started.
    public static bool Foreign => _foreign && _foreignOn != null;

    public static bool Busy
    {
        get
        {
            SurgeryMachine m = SurgeryMachine.instance;
            if (m == null)
            {
                return false;
            }
            if (Foreign && Time.unscaledTime - _foreignSince > ForeignTimeout)
            {
                // The routine died on an error; never hold up the game for it.
                Plugin.Log.LogWarning("[coop] surgery: routine did not finish on this machine");
                _foreign = false;
                Traverse.Create(m).Field("surgeryIsInProgress").SetValue(false);
            }
            return _goAhead || Foreign || Traverse.Create(m).Field<bool>("surgeryIsInProgress").Value;
        }
    }

    public static string CurrentCharm() => SurgeryMachine.GetCurrentlyInspectedPowerup().identifier.ToString();

    public static PowerupScript FusionCharm(string identifier)
    {
        foreach (PowerupScript p in PowerupScript.list_FusionCharms_All)
        {
            if (p != null && p.identifier.ToString() == identifier)
            {
                return p;
            }
        }
        return null;
    }

    // First step of the game's surgery routine on this machine.
    public static Step FirstStep()
    {
        if (_foreignStarting)
        {
            _foreignStarting = false;
            return Step.Proceed;
        }
        if (Coop.Role == Role.Host)
        {
            return Coop.HostPass("surgery", CurrentCharm()) ? Step.Proceed : Step.Cancel;
        }
        if (!_waiting)
        {
            _waiting = true;
            _goAhead = false;
            _refused = false;
            _waitSince = Time.unscaledTime;
            Coop.ClientRequest("surgery", CurrentCharm());
            _refused = !Coop.Link.Connected;
        }
        if (_goAhead)
        {
            _waiting = false;
            _goAhead = false;
            return Step.Proceed;
        }
        if (_refused || SurgeryMachine.IsForceClosing() || Time.unscaledTime - _waitSince > RequestTimeout)
        {
            _waiting = false;
            return Step.Cancel;
        }
        return Step.Wait;
    }

    public static void Refused() => _refused = true;

    // A surgery from the host's list of actions.
    public static string Run(int player, string charm)
    {
        if (player == Coop.Self && _waiting)
        {
            _goAhead = true;
            return "own";
        }
        SurgeryMachine m = SurgeryMachine.instance;
        if (m == null || !PlatformAPI.instance.OwnsDlc1_UnholyFusion())
        {
            return "no surgery machine here";
        }
        int index = PowerupScript.list_FusionCharms_All.IndexOf(FusionCharm(charm));
        if (index < 0)
        {
            return "charm not found";
        }
        Traverse.Create(m).Field("charmsNavigationIndex").SetValue(index);
        _foreign = true;
        _foreignStarting = true;
        _foreignSince = Time.unscaledTime;
        _foreignOn = m;
        m.StartCoroutine(ForeignRoutine(m));
        return "running";
    }

    private static IEnumerator ForeignRoutine(SurgeryMachine m)
    {
        yield return (IEnumerator)AccessTools.Method(typeof(SurgeryMachine), "SurgeryInProgress").Invoke(m, null);
        // Puts the machine back to its idle look and the charms in their places. Its camera move
        // is skipped while this is still set.
        Traverse.Create(m).Field("forceClose_Death").SetValue(false);
        SurgeryMachine.Close();
        _foreign = false;
    }

    // Leaving the machine frees it for the others.
    public static void Tick()
    {
        if (Coop.SurgeryUser != Coop.Self || Coop.Link == null)
        {
            _sawOpen = false;
            _userSince = Time.unscaledTime;
            return;
        }
        if (SurgeryMachine.IsEnabled())
        {
            _sawOpen = true;
            return;
        }
        if (!_sawOpen && Time.unscaledTime - _userSince < 3f)
        {
            return;
        }
        if (Coop.Role == Role.Host)
        {
            Coop.HostPass("surgeryClose", "");
        }
        else if (Coop.Link.Connected)
        {
            Coop.ClientRequest("surgeryClose", "");
        }
    }

    public static void Reset()
    {
        _foreign = false;
        _foreignStarting = false;
        _waiting = false;
        _goAhead = false;
        _refused = false;
        _sawOpen = false;
    }
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_SurgeryMachine_Open))]
internal static class SurgeryOpenGate
{
    private static bool Prefix() => SurgeryMachine.IsEnabled() || Gate.Pass("surgeryOpen", "");
}

// The routine's MoveNext rather than the method that creates it: that one is so small that the
// runtime may build it into its caller, where a patch would never run.
[HarmonyPatch]
internal static class SurgeryRoutineGate
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SurgeryMachine), "SurgeryInProgress"));
    }

    private static bool Prefix(object __instance, ref bool __result)
    {
        if (Coop.Role == Role.Off)
        {
            return true;
        }
        Traverse routine = Traverse.Create(__instance);
        if (routine.Field<int>("<>1__state").Value != 0)
        {
            return true;
        }
        switch (SurgerySync.FirstStep())
        {
            case SurgerySync.Step.Proceed:
                return true;
            case SurgerySync.Step.Wait:
                routine.Field("<>2__current").SetValue(null);
                __result = true;
                return false;
            default:
                __result = false;
                return false;
        }
    }
}

[HarmonyPatch(typeof(FlashScreen), nameof(FlashScreen.SpawnCamera))]
internal static class SurgeryNoFlash
{
    private static bool Prefix() => !SurgerySync.Foreign;
}

[HarmonyPatch(typeof(CameraGame), nameof(CameraGame.FieldOfViewExtraSet))]
internal static class SurgeryNoFieldOfView
{
    private static bool Prefix() => !SurgerySync.Foreign;
}

[HarmonyPatch(typeof(DialogueScript), nameof(DialogueScript.SetDialogue), new[] { typeof(bool), typeof(string[]) })]
internal static class SurgeryNoDialogue
{
    private static bool Prefix() => !SurgerySync.Foreign;
}
