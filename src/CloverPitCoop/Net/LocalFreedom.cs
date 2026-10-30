using HarmonyLib;

namespace CloverPitCoop.Net;

// While someone else sits at the slot machine, the shared game phase is "gambling" on every
// machine (game logic such as charm counters depends on it, so it must stay the same
// everywhere). The players who are not at the machine still get to walk, look around and use
// the room: for the few local, input-side code paths that refuse to work during "gambling",
// the phase reads as "preparation" on their machine. Everything they do still goes through the
// normal relayed actions, which the host runs between two spins.
internal static class LocalFreedom
{
    // Kinds a player who is not at the machine may do while someone else plays.
    public static bool IsRoomAction(string kind)
    {
        switch (kind)
        {
            case "deposit":
            case "interest":
            case "buy":
            case "sell":
            case "drawer":
            case "equip":
                return true;
            default:
                return false;
        }
    }

    public static bool Active =>
        Coop.Role != Role.Off && Coop.MachineOwner >= 0 && Coop.MachineOwner != Coop.Self
        && PhaseRaw() == GameplayMaster.GamePhase.gambling;

    // The machine is between two spins: a room action can run now without landing in the middle
    // of a spin and its charm animations on one machine but not on another.
    public static bool MachineIdle()
    {
        SlotMachineScript.State s = SlotMachineScript.StateGet();
        return !SlotMachineScript.IsSpinning() && !PowerupTriggerAnimController.HasAnimations()
            && (s == SlotMachineScript.State.idle || s == SlotMachineScript.State.off);
    }

    private static GameplayMaster.GamePhase PhaseRaw()
    {
        PhaseSpoof.Suspended = true;
        GameplayMaster.GamePhase p = GameplayMaster.GetGamePhase();
        PhaseSpoof.Suspended = false;
        return p;
    }

    // Sitting down opened the slot machine's menu (lever, red button) as the active menu; on the
    // machines of the others the room's menu must stay active. Taken off the menu stack directly:
    // the menu's own Back() runs its onBack event, which stands the player up (seen in a test:
    // phase went to cutscene on the client only).
    public static void LeaveSlotMenu()
    {
        DiegeticMenuController slot = DiegeticMenuController.SlotMenu;
        if (slot != null && DiegeticMenuController.ActiveMenu == slot)
        {
            DiegeticMenuController.stack.Remove(slot);
            _slotMenuRemoved = true;
            Plugin.Log.LogInfo("[coop] freedom: slot menu taken off the stack (someone else is at the machine)");
        }
    }

    // The round ends with SlotMachineScript.TurnOff -> slot menu Back() -> its onBack stands the
    // player up (FCall_StopPlaying: round-end cutscene, interest, tickets). That must happen on
    // every machine, so the slot menu goes back on top just before.
    private static bool _slotMenuRemoved;

    public static void RestoreSlotMenu()
    {
        if (!_slotMenuRemoved)
        {
            return;
        }
        _slotMenuRemoved = false;
        DiegeticMenuController slot = DiegeticMenuController.SlotMenu;
        if (slot != null && DiegeticMenuController.ActiveMenu != slot)
        {
            DiegeticMenuController.stack.Remove(slot);
            DiegeticMenuController.stack.Add(slot);
            Plugin.Log.LogInfo("[coop] freedom: slot menu back on the stack for the end of the round");
        }
    }
}

internal static class PhaseSpoof
{
    public static bool On;
    public static bool Suspended;

    public static void Begin()
    {
        On = LocalFreedom.Active;
    }

    public static void End()
    {
        On = false;
    }
}

[HarmonyPatch(typeof(SlotMachineScript), nameof(SlotMachineScript.TurnOff))]
internal static class FreedomRoundEnd
{
    private static void Prefix() => LocalFreedom.RestoreSlotMenu();
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.GetGamePhase))]
internal static class PhaseSpoofRead
{
    private static void Postfix(ref GameplayMaster.GamePhase __result)
    {
        if (PhaseSpoof.On && !PhaseSpoof.Suspended && __result == GameplayMaster.GamePhase.gambling)
        {
            __result = GameplayMaster.GamePhase.preparation;
        }
    }
}

// Walking (PlayerScript only moves in "preparation").
[HarmonyPatch(typeof(PlayerScript), "Update")]
internal static class FreedomWalk
{
    private static void Prefix() => PhaseSpoof.Begin();
    private static void Postfix() => PhaseSpoof.End();
}

// The charm inspect menu offers only "cancel" during "gambling"; give the full menu
// (sell, put in drawer, equip) to players who are not at the machine.
[HarmonyPatch(typeof(PowerupScript), nameof(PowerupScript.Inspect))]
internal static class FreedomInspect
{
    private static void Prefix() => PhaseSpoof.Begin();
    private static void Postfix() => PhaseSpoof.End();
}

[HarmonyPatch(typeof(PowerupScript), "_InspectClose")]
internal static class FreedomInspectClose
{
    private static void Prefix() => PhaseSpoof.Begin();
    private static void Postfix() => PhaseSpoof.End();
}

[HarmonyPatch(typeof(DiegeticMenuElement), nameof(DiegeticMenuElement.SetPromptGuide_Powerup))]
internal static class FreedomPrompt
{
    private static void Prefix() => PhaseSpoof.Begin();
    private static void Postfix() => PhaseSpoof.End();
}
