using HarmonyLib;

namespace CloverPitCoop.Net;

// Every game action that changes the shared run goes through one of these prefixes.
// Host: check the rules, number the action and send it, then let it run.
// Client: run only actions replayed from the host; send local ones to the host as requests.
internal static class Gate
{
    public static bool Pass(string kind, string arg)
    {
        if (Coop.Bypass)
        {
            return true;
        }
        switch (Coop.Role)
        {
            case Role.Host:
                return Coop.HostPass(kind, arg);
            case Role.Client:
                if (Coop.Replaying)
                {
                    return true;
                }
                if (kind == "phone")
                {
                    Coop.RefusedClient++;
                    Coop.Show("Not allowed: only the host can use the phone");
                    Plugin.Log.LogInfo("[coop] rule: phone refused locally (host only)");
                    return false;
                }
                Coop.ClientRequest(kind, arg);
                return false;
            default:
                return true;
        }
    }
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_Phone_Pickup))]
internal static class PhoneGate
{
    private static bool Prefix() => Gate.Pass("phone", "");
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_SlotMachineTurnOnTry))]
internal static class TurnOnGate
{
    private static bool Prefix() => Gate.Pass("turnOn", "");
}

[HarmonyPatch(typeof(GameplayMaster), "_GotoGambling_Max")]
internal static class BetMaxGate
{
    private static bool Prefix() => Gate.Pass("bet", "Max");
}

[HarmonyPatch(typeof(GameplayMaster), "_GotoGambling_Mid")]
internal static class BetMidGate
{
    private static bool Prefix() => Gate.Pass("bet", "Mid");
}

[HarmonyPatch(typeof(GameplayMaster), "_GotoGambling_Free")]
internal static class BetFreeGate
{
    private static bool Prefix() => Gate.Pass("bet", "Free");
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_SlotSpinTry))]
internal static class SpinGate
{
    private static bool Prefix(bool calledByAutoSpin) => Gate.Pass("spin", calledByAutoSpin ? "auto" : "");
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_DepositTry))]
internal static class DepositGate
{
    private static bool Prefix()
    {
        // The ATM ignores presses for a second after each deposit (ATMScript.depositButtonDelay,
        // counted per frame on each machine). Only presses that pass the host's timer become an
        // action; replays skip the timer (ATMDelayPatch), so every machine deposits the same times.
        if (Coop.Role == Role.Host && !Coop.Bypass && ATMScript.IsDepositButtonDelayed())
        {
            return false;
        }
        return Gate.Pass("deposit", "");
    }
}

[HarmonyPatch(typeof(ATMScript), nameof(ATMScript.IsDepositButtonDelayed))]
internal static class ATMDelayPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (Coop.Role == Role.Client && Coop.Replaying)
        {
            __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_InterestsGetTry))]
internal static class InterestGate
{
    private static bool Prefix() => Gate.Pass("interest", "");
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_BuyTry))]
internal static class BuyGate
{
    private static bool Prefix(int id) => Gate.Pass("buy", id.ToString());
}

[HarmonyPatch(typeof(PowerupScript), "_InspectThrowAway")]
internal static class SellGate
{
    private static bool Prefix(PowerupScript __instance) => Gate.Pass("sell", __instance.identifier.ToString());
}

[HarmonyPatch(typeof(PowerupScript), "_InspectPutInDrawerTry")]
internal static class DrawerGate
{
    private static bool Prefix(PowerupScript __instance) => Gate.Pass("drawer", __instance.identifier.ToString());
}

[HarmonyPatch(typeof(PowerupScript), "_InspectEquipTry")]
internal static class EquipGate
{
    private static bool Prefix(PowerupScript __instance) => Gate.Pass("equip", __instance.identifier.ToString());
}
