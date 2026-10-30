using System.Collections;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

// Phone sync. Picking up is a normal relayed action ("phone", host only). Inside the phone UI
// every confirmed choice is made in PhoneUiScript.NavigationUpdate: back (menuBack or button 0),
// reroll (button 1) or an ability (buttons 2..6). The host relays each confirmed choice as
// "phoneNav" with the button index ("back" for leaving). Other machines replay it by setting
// the same selection and injecting one confirm press into the game's own NavigationUpdate,
// so the reroll cost, ability rolls and picks run through the game's code everywhere.
// Without a decision, the client's NavigationUpdate is blocked: only the host chooses.
internal static class PhoneNav
{
    // Next choice to apply: "back" or a button index. Set by the network (client) or a bot.
    public static string Decision;
    public static string LastOutcome;
    public static int Consumed;
    public static int NavCalls;

    public static bool HideCursor;

    public static bool UiOpen => PhoneUiScript.IsEnabled();

    // After an ability was picked the phone shows the reply and waits for one more press
    // before it closes. That press has no game effect, so replaying machines press it themselves.
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

    public static IEnumerator WaitClosed()
    {
        float t = 0f;
        while ((UiOpen || PhoneScript.IsOn()) && t < 20f)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
    }
}

[HarmonyPatch(typeof(PhoneUiScript), "NavigationUpdate")]
internal static class PhoneNavPatch
{
    private static bool Prefix(PhoneUiScript __instance, ref bool goBack, ref bool rerolled, ref AbilityScript abilityPicked)
    {
        PhoneNav.NavCalls++;
        string d = PhoneNav.Decision;
        if (d != null)
        {
            // Apply a decision: select the button and press confirm (or back) for this call.
            Traverse sel = Traverse.Create(__instance).Field("selectionIndex");
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
            goBack = false;
            rerolled = false;
            abilityPicked = null;
            return false;
        }
        return true;
    }

    private static void Postfix(PhoneUiScript __instance, bool goBack, bool rerolled, AbilityScript abilityPicked)
    {
        if (PhoneNav.Decision != null)
        {
            PhoneNav.HideCursor = false;
            Inject.Clear();
            PhoneNav.Decision = null;
            PhoneNav.Consumed++;
        }
        string outcome = goBack ? "back" : rerolled ? "reroll" : abilityPicked != null ? "pick" : null;
        if (outcome == null)
        {
            return;
        }
        PhoneNav.LastOutcome = outcome;
        if (Coop.Role == Role.Host)
        {
            int index = Traverse.Create(__instance).Field("selectionIndex").GetValue<int>();
            string arg = goBack ? "back" : index.ToString();
            Coop.HostPass("phoneNav", arg);
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
