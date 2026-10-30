using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop;

// Test for the F8 panel, run with --coop-menutest: stand in the room, open the panel, check
// that the system cursor is visible and free and the game's input reads are blocked, close
// the panel and check that the cursor is locked again.
internal static class MenuTest
{
    public static IEnumerator Run()
    {
        float t = 0f;
        while (t < 240f && !(GameplayMaster.instance != null && GameplayMaster.GetGamePhase() == GameplayMaster.GamePhase.preparation && !Bot.DialogueOrMenuOpen()))
        {
            t += Time.unscaledDeltaTime;
            bool title = GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen();
            if (GameplayMaster.instance == null || title || Bot.DialogueOrMenuOpen())
            {
                Bot.Enabled = true;
                Bot.TickDialogues();
            }
            yield return null;
        }
        Bot.Enabled = false;
        yield return new WaitForSecondsRealtime(2f);

        CursorLockMode lockBefore = Cursor.lockState;
        bool visibleBefore = Cursor.visible;
        int patched = Harmony.GetAllPatchedMethods().Count(m => m.DeclaringType == typeof(Controls)
            && Harmony.GetPatchInfo(m).Prefixes.Any(p => p.owner == Plugin.Guid && p.PatchMethod.DeclaringType.Name.StartsWith("InputBlock")));

        Net.CoopMenu.SetOpen(true);
        yield return new WaitForSecondsRealtime(1f);
        bool cursorFree = Cursor.visible && Cursor.lockState == CursorLockMode.None;
        bool blocked = Net.InputBlock.Active;
        yield return new WaitForSecondsRealtime(1f);

        Net.CoopMenu.SetOpen(false);
        yield return new WaitForSecondsRealtime(0.5f);
        bool restored = Cursor.lockState == lockBefore && Cursor.visible == visibleBefore;

        bool pass = cursorFree && blocked && patched > 0 && restored;
        Plugin.Log.LogInfo($"[coop] MENUTEST {(pass ? "PASS" : "FAIL")}: before lock={lockBefore} visible={visibleBefore}; " +
            $"open: cursorFree={cursorFree} inputBlocked={blocked} patchedReads={patched}; closed: restored={restored} " +
            $"(lock={Cursor.lockState} visible={Cursor.visible})");
        Application.Quit();
    }
}
