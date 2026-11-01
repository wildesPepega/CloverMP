using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

// Phone sync. Only the player who picks up gets the phone window and the camera move; everyone
// else stays where they are and runs the same game logic without any of that (PhoneHeadless).
// Picking up is a relayed action ("phone"). Inside the phone every confirmed choice is made in
// PhoneUiScript.NavigationUpdate: back (menuBack or button 0), reroll (button 1) or an ability
// (buttons 2..6), relayed as "phoneNav" with the button index ("back" for leaving). Every machine
// applies it by setting the same selection and injecting one confirm press into the game's own
// NavigationUpdate, so the reroll cost, ability rolls and picks run through the game's code.
// A friend on the phone only hovers locally; a confirm goes to the host as a request and takes
// effect when the host's broadcast comes back, like every other action.
internal static class PhoneNav
{
    // Next choice to apply: "back" or a button index. Set by the network (client) or a bot.
    public static string Decision;
    public static string LastOutcome;
    public static int Consumed;
    public static int NavCalls;

    public static bool HideCursor;

    // A friend on the phone: confirm presses do not reach the game's NavigationUpdate.
    public static bool SuppressConfirm;
    // That friend's choice is on its way to the host.
    public static bool AwaitingHost;
    // The last replayed choice ran without the phone window.
    public static bool LastWasHeadless;

    // This machine picked up and its phone window has not rolled the abilities yet (that happens
    // after the pick-up animation). Nothing else may run before that, or the rolls could see a
    // different state than on the other machines, which roll right away.
    private static float _startedAt = -100f;
    public static bool Starting => _startedAt > 0f && Time.unscaledTime - _startedAt < 15f;

    public static void MarkStarting() => _startedAt = Time.unscaledTime;

    public static void ClearStarting() => _startedAt = -100f;

    public static bool UiOpen => PhoneUiScript.IsEnabled();

    // After an ability was picked the phone shows the reply and waits for one more press
    // before it closes. That press has no game effect; test bots press it themselves.
    public static IEnumerator DismissReply()
    {
        float t = 0f;
        float next = 0f;
        while (UiOpen && t < 20f)
        {
            t += Time.unscaledDeltaTime;
            if (t >= next && Decision == null)
            {
                next = t + 0.3f;
                Inject.Press(Controls.InputAction.menuSelect);
            }
            yield return null;
        }
    }

    // Real players read the reply as long as they like.
    public static IEnumerator WaitClosed()
    {
        float limit = Coop.Automated ? 20f : 120f;
        float t = 0f;
        while ((UiOpen || PhoneScript.IsOn()) && t < limit)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
    }
}

// The phone's game logic for everyone who is not on the phone: same rolls, same costs, same
// picks, but no window, camera, sound, flash or shake.
internal static class PhoneHeadless
{
    // Sounds, flashes, shakes and vibration of the phone code stay silent while this is set.
    public static bool Quiet;

    public static void Pickup()
    {
        GameplayData data = GameplayData.Instance;
        PhoneUiScript ui = PhoneUiScript.instance;
        if (data == null || ui == null)
        {
            return;
        }
        if (PhoneScript.HasNoDialogue())
        {
            // No call waiting: picking up only marks the phone as used.
            data._phone_pickedUpOnceLastDeadline = true;
            return;
        }
        Quiet = true;
        try
        {
            data._phone_PickupWithAbilities_OverallCounter++;
            AccessTools.Method(typeof(PhoneUiScript), "DefinePhoneStuff").Invoke(ui, new object[] { false });
        }
        catch (Exception e)
        {
            // The rolls come before the window texts, so the state is usually complete. A real
            // difference is caught by the next hash check and corrected by the host.
            data._phone_pickedUpOnceLastDeadline = true;
            Plugin.Log.LogWarning("[coop] phone: pick-up without window failed: " + (e.InnerException ?? e).Message);
        }
        finally
        {
            Quiet = false;
        }
    }

    public static string Nav(string arg)
    {
        GameplayData data = GameplayData.Instance;
        PhoneUiScript ui = PhoneUiScript.instance;
        if (data == null || ui == null)
        {
            return "no phone";
        }
        PhoneNav.LastWasHeadless = true;
        PhoneNav.Decision = arg;
        Quiet = true;
        var args = new object[] { data, false, false, null };
        try
        {
            AccessTools.Method(typeof(PhoneUiScript), "NavigationUpdate").Invoke(ui, args);
        }
        catch (Exception e)
        {
            // Never let this stop the action loop; the host's next hash check corrects the state.
            Plugin.Log.LogWarning("[coop] phone: choice without window failed: " + (e.InnerException ?? e).Message);
            PhoneNavPatch.RestoreInput();
            return "failed";
        }
        finally
        {
            Quiet = false;
            PhoneNav.Decision = null;
        }
        bool goBack = (bool)args[1];
        bool picked = args[3] != null;
        if (goBack || picked)
        {
            // What the phone window does when it closes.
            data._phone_pickedUpOnceLastDeadline = true;
            Traverse.Create(ui).Field("selectionIndex").SetValue(-1);
            if (Coop.Role == Role.Host)
            {
                _ = Data.SaveGame(Data.GameSavingReason.phoneSaveTime);
            }
        }
        return goBack ? "back" : picked ? "pick" : (bool)args[2] ? "reroll" : "no effect";
    }
}

[HarmonyPatch]
internal static class PhoneQuietSound
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo m in typeof(Sound).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name == "Play" || m.Name == "Play_Unpausable" || m.Name == "Play3D" || m.Name == "Play3D_Unpausable")
            {
                yield return m;
            }
        }
    }

    private static bool Prefix() => !PhoneHeadless.Quiet;
}

[HarmonyPatch(typeof(FlashScreen), nameof(FlashScreen.SpawnCamera))]
internal static class PhoneQuietFlash
{
    private static bool Prefix() => !PhoneHeadless.Quiet;
}

[HarmonyPatch(typeof(CameraGame), nameof(CameraGame.Shake))]
internal static class PhoneQuietShake
{
    private static bool Prefix() => !PhoneHeadless.Quiet;
}

[HarmonyPatch(typeof(Controls), nameof(Controls.VibrationSet_PreferMax))]
internal static class PhoneQuietVibration
{
    private static bool Prefix() => !PhoneHeadless.Quiet;
}

[HarmonyPatch(typeof(PhoneUiScript), "NavigationUpdate")]
internal static class PhoneNavPatch
{
    private static bool _blockSaved;
    private static bool _blockSet;
    private static bool _pressedConfirm;
    private static bool _pressedBack;
    private static int _selectionBefore;

    private static bool Prefix(PhoneUiScript __instance, ref bool goBack, ref bool rerolled, ref AbilityScript abilityPicked)
    {
        PhoneNav.NavCalls++;
        PhoneNav.ClearStarting();
        Traverse sel = Traverse.Create(__instance).Field("selectionIndex");
        string d = PhoneNav.Decision;
        if (d != null)
        {
            // Apply a decision: select the button and press confirm (or back) for this call.
            // The player's own input (mouse, walking keys bound to menu axes) must not interfere.
            _blockSaved = InputBlock.Active;
            _blockSet = true;
            InputBlock.Active = true;
            if (d == "back")
            {
                Inject.Press(Controls.InputAction.menuBack);
            }
            else
            {
                sel.SetValue(int.Parse(d));
                Inject.Press(Controls.InputAction.menuSelect);
            }
            PhoneNav.HideCursor = true;
            return true;
        }
        if (Coop.Role == Role.Client && !Coop.Bypass)
        {
            // A friend on the phone: hovering works, a confirm becomes a request (see Postfix).
            _pressedConfirm = Controls.ActionButton_PressedGet(0, Controls.InputAction.menuSelect);
            _pressedBack = Controls.ActionButton_PressedGet(0, Controls.InputAction.menuBack);
            _selectionBefore = sel.GetValue<int>();
            PhoneNav.SuppressConfirm = true;
        }
        return true;
    }

    // Undoes what the Prefix set up for applying a decision.
    public static void RestoreInput()
    {
        if (_blockSet)
        {
            InputBlock.Active = _blockSaved;
            _blockSet = false;
        }
        PhoneNav.HideCursor = false;
        Inject.Clear();
    }

    private static void Postfix(PhoneUiScript __instance, bool goBack, bool rerolled, AbilityScript abilityPicked)
    {
        Traverse sel = Traverse.Create(__instance).Field("selectionIndex");
        if (PhoneNav.SuppressConfirm)
        {
            PhoneNav.SuppressConfirm = false;
            RequestChoice(sel.GetValue<int>());
            return;
        }
        if (PhoneNav.Decision != null)
        {
            RestoreInput();
        }
        if (PhoneNav.Decision != null)
        {
            PhoneNav.Decision = null;
            PhoneNav.Consumed++;
            PhoneNav.AwaitingHost = false;
        }
        string outcome = goBack ? "back" : rerolled ? "reroll" : abilityPicked != null ? "pick" : null;
        if (outcome == null)
        {
            return;
        }
        PhoneNav.LastOutcome = outcome;
        if (Coop.Role == Role.Host)
        {
            string arg = goBack ? "back" : sel.GetValue<int>().ToString();
            Coop.HostPass("phoneNav", arg);
        }
        if (goBack || abilityPicked != null)
        {
            Coop.PhoneUser = -1;
        }
    }

    private static void RequestChoice(int selection)
    {
        if (PhoneNav.AwaitingHost)
        {
            return;
        }
        string arg = null;
        if (_pressedBack || (_pressedConfirm && _selectionBefore == 0 && selection == 0))
        {
            arg = "back";
        }
        else if (_pressedConfirm && _selectionBefore >= 0 && selection >= 0)
        {
            if (selection == 1 && GameplayData.CloverTicketsGet() < GameplayData.PhoneRerollCostGet())
            {
                CameraGame.Shake(1f);
                Sound.Play("SoundMenuError");
                return;
            }
            arg = selection.ToString();
        }
        if (arg != null)
        {
            PhoneNav.AwaitingHost = true;
            Coop.ClientRequest("phoneNav", arg);
        }
    }
}

// While a phone decision is applied, the mouse must not move the selection.
[HarmonyPatch(typeof(VirtualCursors), nameof(VirtualCursors.IsCursorVisible))]
internal static class PhoneCursorPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (PhoneNav.HideCursor)
        {
            __result = false;
            return false;
        }
        return true;
    }
}

// One-frame input injection used by the mod itself (not only by test bots).
internal static class Inject
{
    private static Controls.InputAction? _action;
    private static int _frame = -1;

    public static void Press(Controls.InputAction action)
    {
        _action = action;
        _frame = Time.frameCount;
    }

    public static void Clear()
    {
        _action = null;
    }

    // Same rule as the test bot: valid in its frame, carried to the next frame once if nobody
    // read it in its own frame (readers that run before the mod's coroutines).
    private static int _readFrame = -1;

    public static bool IsPressed(Controls.InputAction action)
    {
        if (_action != action)
        {
            return false;
        }
        int f = Time.frameCount;
        bool live = f == _frame || (f == _frame + 1 && _readFrame != _frame);
        if (live)
        {
            _readFrame = f;
        }
        return live;
    }
}
