using System.Collections;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop;

// Test bot: injects menu presses and calls game actions directly, so a run can be
// driven without a human. Only active while a self-test coroutine sets Enabled.
internal static class Bot
{
    public static bool Enabled;

    private static Controls.InputAction? _pressThisFrame;
    private static int _pressFrame = -1;
    private static float _nextPress;

    public static void Press(Controls.InputAction action)
    {
        _pressThisFrame = action;
        _pressFrame = Time.frameCount;
    }

    // A press counts for the frame it was made in. If nothing read it in that frame (the
    // reader ran earlier in the frame than the bot's coroutine), it carries over to the next
    // frame once. It never counts in both frames, so one press cannot act twice.
    private static int _readFrame = -1;

    internal static bool IsPressed(Controls.InputAction action)
    {
        if (!Enabled || _pressThisFrame != action)
        {
            return false;
        }
        int f = Time.frameCount;
        bool live = f == _pressFrame || (f == _pressFrame + 1 && _readFrame != _pressFrame);
        if (live)
        {
            _readFrame = f;
        }
        return live;
    }

    // Clicks through intro videos and dialogues; answers "no" to questions (skips the tutorial).
    public static void TickDialogues()
    {
        if (Time.unscaledTime < _nextPress)
        {
            return;
        }
        _nextPress = Time.unscaledTime + 0.3f;
        if (DialogueScript.instance != null && DialogueScript.IsEnabled() && DialogueScript.IsAskingQuestion())
        {
            Plugin.Instance.StartCoroutine(AnswerNo());
            _nextPress = Time.unscaledTime + 1f;
            return;
        }
        Press(Controls.InputAction.menuSelect);
    }

    private static IEnumerator AnswerNo()
    {
        Press(Controls.InputAction.menuBack);
        yield return null;
        yield return null;
        Press(Controls.InputAction.menuSelect);
    }

    public static bool DialogueOrMenuOpen()
    {
        return (DialogueScript.instance != null && DialogueScript.IsEnabled())
            || ScreenMenuScript.IsEnabled();
    }

    // Same choice a player makes in the bet menu after sitting down.
    public static void ChooseBet()
    {
        ScreenMenuScript.Close(false);
        string handler = GameplayData.GetHypotehticalMaxSpinsBuyable() <= 0 ? "_GotoGambling_Free" : "_GotoGambling_Max";
        Plugin.Log.LogInfo($"[coop] bot: bet via {handler}");
        AccessTools.Method(typeof(GameplayMaster), handler).Invoke(GameplayMaster.instance, null);
    }
}

[HarmonyPatch(typeof(Controls), nameof(Controls.ActionButton_PressedGet),
    new[] { typeof(int), typeof(Controls.InputAction), typeof(bool) })]
internal static class BotInputPatch
{
    private static void Postfix(Controls.InputAction action, ref bool __result)
    {
        if (Net.PhoneNav.SuppressConfirm && (action == Controls.InputAction.menuSelect || action == Controls.InputAction.menuBack))
        {
            __result = false;
            return;
        }
        if (!__result && (Bot.IsPressed(action) || Net.Inject.IsPressed(action)))
        {
            __result = true;
        }
    }
}
