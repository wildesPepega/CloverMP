using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

// While the F8 co-op panel is open the game must not react to mouse, keyboard or gamepad,
// and the system mouse cursor is shown and free so the panel can be clicked.
// All of the game's input reads go through Panik.Controls; every read that takes a PlayerExt
// (the int-index overloads forward to these) answers "nothing pressed / no movement".
// Presses the mod itself injects (Bot, Inject) are added later by a postfix on the int
// overload and are not affected.
internal static class InputBlock
{
    public static bool Active;

    private static bool _cursorSaved;
    private static bool _oldVisible;
    private static CursorLockMode _oldLock;

    public static void Set(bool active)
    {
        if (active == Active)
        {
            return;
        }
        Active = active;
        if (active)
        {
            _oldVisible = Cursor.visible;
            _oldLock = Cursor.lockState;
            _cursorSaved = true;
        }
        else if (_cursorSaved)
        {
            Cursor.visible = _oldVisible;
            Cursor.lockState = _oldLock;
            _cursorSaved = false;
        }
    }

    // Called every frame from OnGUI while the panel is open, in case the game locks it again.
    public static void KeepCursorFree()
    {
        if (!Active)
        {
            return;
        }
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }
}

internal static class InputBlockTargets
{
    public static readonly HashSet<string> Names = new HashSet<string>
    {
        "ActionButton_PressedGet", "ActionButton_ReleasedGet", "ActionButton_HoldGet", "ActionButton_HoldPreviousGet",
        "ActionAxis_GetValue", "ActionAxis_GetValuePrevious",
        "ActionAxisPair_GetValue", "ActionAxisPair_GetValuePrevious",
        "MouseButton_PressedGet", "MouseButton_ReleasedGet", "MouseButton_HoldGet",
        "MouseAxis_ValueGet", "MouseAxis_ValuePreviousGet",
        "JoystickButton_PressedGet", "JoystickButton_ReleasedGet", "JoystickButton_HoldGet",
        "JoystickAxis_ValueGet", "JoystickAxis_ValuePreviousGet",
        "KeyboardButton_PressedGet", "KeyboardButton_ReleasedGet", "KeyboardButton_HoldGet",
    };

    public static IEnumerable<MethodBase> Of(System.Type returnType)
    {
        foreach (MethodInfo m in typeof(Controls).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            ParameterInfo[] p = m.GetParameters();
            if (Names.Contains(m.Name) && p.Length > 0 && p[0].ParameterType == typeof(Controls.PlayerExt) && m.ReturnType == returnType)
            {
                yield return m;
            }
        }
    }
}

[HarmonyPatch]
internal static class InputBlockBool
{
    private static IEnumerable<MethodBase> TargetMethods() => InputBlockTargets.Of(typeof(bool));

    private static bool Prefix(ref bool __result)
    {
        if (!InputBlock.Active)
        {
            return true;
        }
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class InputBlockFloat
{
    private static IEnumerable<MethodBase> TargetMethods() => InputBlockTargets.Of(typeof(float));

    private static bool Prefix(ref float __result)
    {
        if (!InputBlock.Active)
        {
            return true;
        }
        __result = 0f;
        return false;
    }
}

[HarmonyPatch(typeof(Controls), nameof(Controls.MenuDirectionalAny_PressedGet))]
internal static class InputBlockMenuDirectional
{
    private static bool Prefix(ref bool __result)
    {
        if (!InputBlock.Active)
        {
            return true;
        }
        __result = false;
        return false;
    }
}
