using System;
using System.Collections;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
using UnityEngine;

namespace CloverPitCoop;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "coop-anything.cloverpit-coop";
    public const string Name = "CloverPit Co-op";
    public const string Version = "0.7.10";

    internal static ManualLogSource Log;
    internal static Plugin Instance;
    internal static BepInEx.Configuration.ConfigEntry<bool> DiscordSetting;
    internal static BepInEx.Configuration.ConfigEntry<bool> PhoneEveryoneSetting;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Log.LogInfo($"[coop] {Name} {Version} loaded");

        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Log.LogInfo("[coop] Harmony patches applied");

        // Discord status (coins, tickets, deadline); on by default, switchable in the F8 menu.
        DiscordSetting = Config.Bind("Discord", "RichPresence", true,
            "Show coins, clover tickets and the deadline as your Discord status.");
        Net.DiscordPresence.Enabled = DiscordSetting.Value;
        PhoneEveryoneSetting = Config.Bind("Coop", "PhoneEveryone", false,
            "When hosting: everyone can use the phone (false: host only).");
        Net.Coop.PhoneEveryone = PhoneEveryoneSetting.Value;
        Net.AutoUpdate.Enabled = Config.Bind("Updates", "AutoUpdate", true,
            "When the game starts: look for a newer CloverMP release on GitHub, install it and restart the game.");
        Net.AutoUpdate.LastTried = Config.Bind("Updates", "LastTried", "",
            "Set by the mod: the last version it installed, which it never tries twice.");
        // Never in test runs: those must stay on the version being tested.
        if (!Array.Exists(Environment.GetCommandLineArgs(), a => a.StartsWith("--coop-", StringComparison.Ordinal)))
        {
            StartCoroutine(Net.AutoUpdate.Run());
        }
        if (!HasArg("--coop-dettest") && !HasArg("--coop-join") && !HasArg("--coop-join-steam") || HasArg("--coop-discordtest"))
        {
            Net.DiscordPresence.Start();
        }

        if (ArgValue("--coop-host") is string hostPort)
        {
            Net.Coop.Automated = true;
            Net.Coop.StartHost(int.Parse(hostPort));
        }
        else if (ArgValue("--coop-join") is string joinPort)
        {
            Net.Coop.Automated = true;
            if (ArgValue("--coop-mutate-skip") is string skip)
            {
                Net.Coop.MutateSkipSeq = int.Parse(skip);
            }
            Net.Coop.StartClient("127.0.0.1", int.Parse(joinPort));
        }

        if (HasArg("--coop-menutest"))
        {
            StartCoroutine(MenuTest.Run());
        }
        else if (HasArg("--coop-selftest"))
        {
            StartCoroutine(SelfTest.Run());
        }
        else if (ArgValue("--coop-dettest") is string label)
        {
            StartCoroutine(DetTest.Run(label));
        }
    }

    // Test runs over Steam relay (--coop-host-steam / --coop-join-steam): both copies use the
    // same account, the client connects to its own SteamID. Started once Steam is up.
    private bool _steamTestStarted;

    private void StartSteamTestIfAsked()
    {
        if (_steamTestStarted || !Steamworks.SteamClient.IsValid)
        {
            return;
        }
        if (HasArg("--coop-host-steam"))
        {
            _steamTestStarted = true;
            Net.Coop.Automated = true;
            Net.Coop.StartHost(Net.SteamLink.Host(), "Steam relay (test)");
        }
        else if (HasArg("--coop-join-steam"))
        {
            _steamTestStarted = true;
            Net.Coop.Automated = true;
            if (ArgValue("--coop-mutate-skip") is string skip)
            {
                Net.Coop.MutateSkipSeq = int.Parse(skip);
            }
            Net.Coop.StartClient(Net.SteamLink.Join(Steamworks.SteamClient.SteamId), "Steam relay to own SteamID (test)");
        }
    }

    private void Update()
    {
        StartSteamTestIfAsked();
        Net.SteamSession.Init();
        Net.Coop.Tick();
        Net.Social.Tick();
        Net.Avatars.Tick();
        Net.Spectate.Tick();
        Net.ClientTest.Tick();
        Net.DiscordPresence.Tick();
    }

    private void OnDestroy()
    {
        Net.Coop.Link?.Dispose();
        Net.DiscordPresence.Stop();
    }

    private void OnApplicationQuit()
    {
        // While Steam still runs, so the others hear of it right away.
        try
        {
            Net.SteamSession.CurrentLobby?.Leave();
            Net.Coop.Link?.Dispose();
        }
        catch (System.Exception e)
        {
            Log.LogWarning("[coop] quit: " + e.Message);
        }
        Net.DiscordPresence.Stop();
    }

    private void OnGUI()
    {
        Net.InputBlock.WatchKeys(Event.current);
        // One broken screen must not take the others down with it, or leave the game's input blocked.
        SafeGui(Net.CoopMenu.OnGUI);
        SafeGui(Net.TabList.OnGUI);
        SafeGui(Net.Social.OnGUI);
        SafeGui(Net.Spectate.OnGUI);
        GUI.color = Color.white;
        if (Net.Coop.Toast == null || Time.unscaledTime > Net.Coop.ToastUntil)
        {
            return;
        }
        Net.UiTheme.Ensure();
        float left = Net.Coop.ToastUntil - Time.unscaledTime;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(left / 0.4f));
        float w = Net.UiTheme.Px(560f);
        GUI.Box(new Rect((Screen.width - w) / 2f, Net.UiTheme.Px(36f), w, Net.UiTheme.Px(52f)), Net.UiTheme.Plain(Net.Coop.Toast), Net.UiTheme.Toast);
        GUI.color = Color.white;
    }

    private string _lastGuiError;

    private void SafeGui(Action screen)
    {
        try
        {
            screen();
        }
        catch (ExitGUIException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (_lastGuiError != e.Message)
            {
                _lastGuiError = e.Message;
                Log.LogError($"[coop] ui: {screen.Method.DeclaringType?.Name}: {e}");
            }
            GUI.color = Color.white;
            GUI.enabled = true;
        }
    }

    internal static bool HasArg(string name)
    {
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static string ArgValue(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}

// Hook check: proves Harmony reaches the game's phase machine and action entry points.
[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.SetGamePhase))]
internal static class SetGamePhasePatch
{
    private static void Prefix(out GameplayMaster.GamePhase __state)
    {
        __state = GameplayMaster.GetGamePhase();
    }

    private static void Postfix(GameplayMaster.GamePhase phase, GameplayMaster.GamePhase __state)
    {
        Net.Coop.OnPhaseChanged(__state, GameplayMaster.GetGamePhase());
        if (__state == GameplayMaster.GetGamePhase())
        {
            return;
        }
        Plugin.Log.LogInfo($"[coop] phase -> {phase}");
        if (phase == GameplayMaster.GamePhase.preparation)
        {
            SelfTest.SawPreparation = true;
        }
        if (phase == GameplayMaster.GamePhase.gambling)
        {
            SelfTest.SawGambling = true;
        }
    }
}

[HarmonyPatch(typeof(GameplayMaster), nameof(GameplayMaster.FCall_SlotSpinTry))]
internal static class SlotSpinTryPatch
{
    private static void Prefix(bool calledByAutoSpin)
    {
        SelfTest.SpinHookCalls++;
        Plugin.Log.LogInfo($"[coop] FCall_SlotSpinTry(auto={calledByAutoSpin})");
    }
}
