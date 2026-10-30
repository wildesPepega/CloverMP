using HarmonyLib;

namespace CloverPitCoop.Net;

// When someone else sits at the slot machine, this machine does not get pulled into the
// machine views: the camera stays where it is (free look keeps working, CameraController lets
// the free camera turn in every phase except the intro). Only the player at the machine sees
// the machine close up. Shared cutscenes (ATM, tickets, trapdoor at the round's end) still
// move everyone's camera.
[HarmonyPatch(typeof(CameraController), nameof(CameraController.SetPosition))]
internal static class CameraPatch
{
    public static int Skipped;

    private static bool Prefix(CameraController.PositionKind kind)
    {
        if (Coop.Role == Role.Off || Coop.MachineOwner < 0 || Coop.MachineOwner == Coop.Self)
        {
            return true;
        }
        switch (kind)
        {
            case CameraController.PositionKind.Slot_Fixed:
            case CameraController.PositionKind.SlotFromTop:
            case CameraController.PositionKind.SlotScreenCloseUp:
            case CameraController.PositionKind.SlotCoinPlate_Fixed:
                Skipped++;
                Plugin.Log.LogInfo($"[coop] camera: kept own view instead of {kind} ({Social.NameOf(Coop.MachineOwner)} is at the machine)");
                return false;
            default:
                return true;
        }
    }
}
