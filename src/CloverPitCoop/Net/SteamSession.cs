using System;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace CloverPitCoop.Net;

// Steam lobby around the relay link: the host opens a friends-only lobby and invites friends
// through the Steam overlay; a friend who accepts (or uses "Join game") enters the lobby and
// connects to the lobby owner over the relay.
internal static class SteamSession
{
    public const string LobbyKey = "cloverpit-coop";
    public const int MaxPlayers = 4;

    public static Lobby? CurrentLobby;
    public static string Status = "";
    private static bool _subscribed;
    private static SteamId _hostId;

    // Called once Steam is up: listen for invites, and handle a launch through an invite
    // (Steam starts the game with +connect_lobby <id>).
    public static void Init()
    {
        if (_subscribed || !SteamClient.IsValid)
        {
            return;
        }
        _subscribed = true;
        SteamFriends.OnGameLobbyJoinRequested += (lobby, friend) => Join(lobby.Id);
        // Faster than waiting for the connection to time out when the host closes the game.
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, member) =>
        {
            if (IsHostOfMyLobby(lobby, member))
            {
                Coop.HostGone("left the lobby");
            }
        };
        SteamMatchmaking.OnLobbyMemberDisconnected += (lobby, member) =>
        {
            if (IsHostOfMyLobby(lobby, member))
            {
                _hostDroppedAt = Time.unscaledTime;
            }
        };
        if (Plugin.ArgValue("+connect_lobby") is string id && ulong.TryParse(id, out ulong lobbyId))
        {
            Join(lobbyId);
        }
    }

    public static async void Host()
    {
        if (Coop.Role != Role.Off)
        {
            return;
        }
        Status = "Creating lobby …";
        Lobby? created = await SteamMatchmaking.CreateLobbyAsync(MaxPlayers);
        if (!created.HasValue)
        {
            Status = "Could not create the lobby";
            return;
        }
        Lobby lobby = created.Value;
        lobby.SetFriendsOnly();
        lobby.SetJoinable(true);
        lobby.SetData(LobbyKey, Plugin.Version);
        CurrentLobby = lobby;
        Coop.StartHost(SteamLink.Host(), "Steam relay");
        Status = "Hosting – invite your friends";
        Invite();
    }

    public static void Invite()
    {
        if (CurrentLobby.HasValue)
        {
            SteamFriends.OpenGameInviteOverlay(CurrentLobby.Value.Id);
        }
    }

    public static async void Join(SteamId lobbyId)
    {
        if (Coop.Role != Role.Off)
        {
            Coop.Show("You are already in a co-op session");
            return;
        }
        Status = "Joining …";
        Lobby? joined = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
        if (!joined.HasValue)
        {
            Status = "Joining failed";
            return;
        }
        Lobby lobby = joined.Value;
        string version = lobby.GetData(LobbyKey);
        if (string.IsNullOrEmpty(version))
        {
            lobby.Leave();
            Status = "This is not a CloverPit Co-op lobby";
            return;
        }
        if (version != Plugin.Version)
        {
            Coop.Show($"Warning: the host has mod version {version}, you have {Plugin.Version}");
        }
        CurrentLobby = lobby;
        SteamId owner = lobby.Owner.Id;
        _hostId = owner;
        _hostDroppedAt = null;
        Coop.StartClient(SteamLink.Join(owner), $"Steam relay to {lobby.Owner.Name}");
        Status = $"Connected to {lobby.Owner.Name}";
    }

    public static void Leave()
    {
        CurrentLobby?.Leave();
        CurrentLobby = null;
        _hostDroppedAt = null;
        if (Coop.Role != Role.Off)
        {
            Coop.Stop();
        }
        Status = "Playing alone";
    }

    // A friend's own run comes back by leaving (Coop.Stop); the host or a player alone goes
    // through saving first.
    public static void ToMainMenu()
    {
        bool playingHostsRun = JoinSync.InCoopRun;
        Leave();
        if (!playingHostsRun)
        {
            Plugin.Instance.StartCoroutine(JoinSync.ToMainMenu());
        }
    }

    // The host is out of the Steam lobby. Lost contact alone can also be a short network problem,
    // so that only counts together with a broken link, and only for a while.
    private static float? _hostDroppedAt;
    private const float HostDroppedWindow = 60f;

    public static bool HostDroppedFromLobby => _hostDroppedAt.HasValue && Time.unscaledTime - _hostDroppedAt.Value < HostDroppedWindow;

    private static bool IsHostOfMyLobby(Lobby lobby, Friend member)
    {
        return Coop.Role == Role.Client && CurrentLobby.HasValue && CurrentLobby.Value.Id == lobby.Id && member.Id == _hostId;
    }
}

// F8: small co-op panel (IMGUI, independent of the game's input system).
internal static class CoopMenu
{
    private static bool _open;
    private static bool _confirmMainMenu;

    public static bool IsOpen => _open;

    // In the game scene, past the intro and title screen (not in the logo videos, the title
    // screen or the run menu).
    public static bool InGame()
    {
        GameplayMaster.GamePhase p = GameplayMaster.GetGamePhase();
        return GameplayMaster.instance != null && p != GameplayMaster.GamePhase.Undefined && p != GameplayMaster.GamePhase.intro
            && !(GeneralUiScript.instance != null && GeneralUiScript.instance.IsShowingTitleScreen())
            && !(ScreenMenuScript.IsEnabled() && GameplayMaster.instance != null && !GeneralUiScript.instance.HasFadedIn());
    }

    public static void SetOpen(bool open)
    {
        _open = open;
        InputBlock.Set(open);
    }

    public static void OnGUI()
    {
        Event e = Event.current;
        if (_open && !InGame())
        {
            SetOpen(false);
        }
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F8 && !_open && !InGame())
        {
            Coop.Show("The co-op menu is available once you are in the room");
            e.Use();
            return;
        }
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.F8 || (_open && e.keyCode == KeyCode.Escape)))
        {
            _open = !_open;
            InputBlock.Set(_open);
            e.Use();
        }
        if (!_open)
        {
            _confirmMainMenu = false;
            return;
        }
        InputBlock.KeepCursorFree();
        UiTheme.Ensure();

        // Dim the game behind the panel.
        GUI.color = new UnityEngine.Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = UnityEngine.Color.white;

        const float w = 480f;
        GUILayout.BeginArea(new Rect(36f, Mathf.Max(36f, Screen.height * 0.5f - 230f), w, Screen.height - 72f));
        GUILayout.BeginVertical(UiTheme.Panel);

        GUILayout.BeginHorizontal();
        GUILayout.Label(UiTheme.Colored("♣ ", UiTheme.Green) + "CLOVERMP", UiTheme.Title);
        GUILayout.FlexibleSpace();
        GUILayout.Label("v" + Plugin.Version, UiTheme.Small, GUILayout.ExpandWidth(false));
        GUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(SteamSession.Status))
        {
            GUILayout.Label(UiTheme.Plain(SteamSession.Status), UiTheme.Small);
        }

        if (!SteamClient.IsValid)
        {
            GUILayout.Label("PLAYERS", UiTheme.Section);
            GUILayout.Label(UiTheme.Colored("Steam is not connected.", UiTheme.Red), UiTheme.Label);
        }
        else if (Coop.Role == Role.Off)
        {
            GUILayout.Label("PLAY", UiTheme.Section);
            GUILayout.Label("Alone in the room. Start a session for your friends:", UiTheme.Label);
            if (GUILayout.Button("HOST CO-OP", UiTheme.Button))
            {
                SteamSession.Host();
            }
            GUILayout.Label("To join: accept a friend's invite in Steam once you are in the room.", UiTheme.Small);
        }
        else
        {
            GUILayout.Label("PLAYERS", UiTheme.Section);
            PlayerRow(Coop.HostPlayer);
            foreach (int p in Coop.Role == Role.Host ? Coop.Players : Social.KnownPlayers())
            {
                PlayerRow(p);
            }

            GUILayout.Label("SLOT MACHINE", UiTheme.Section);
            GUILayout.Label(Coop.MachineOwner >= 0
                ? $"{Social.ColoredName(Coop.MachineOwner)} · SPINS LEFT: {GameplayData.SpinsLeftGet()} · {UiTheme.Coins(GameplayData.CoinsGet())}"
                : UiTheme.Colored("FREE", UiTheme.Green), UiTheme.Label);

            GUILayout.Space(10f);
            if (Coop.Role == Role.Host && GUILayout.Button("INVITE FRIENDS", UiTheme.Button))
            {
                SteamSession.Invite();
            }
            if (GUILayout.Button("LEAVE CO-OP", UiTheme.ButtonGhost))
            {
                SteamSession.Leave();
            }
        }

        GUILayout.Label("PHONE", UiTheme.Section);
        GUILayout.BeginHorizontal();
        GUILayout.Label(Coop.PhoneEveryone ? "Everyone can use it" : "Host only", UiTheme.Label);
        GUILayout.FlexibleSpace();
        if (Coop.Role != Role.Client
            && GUILayout.Button(Coop.PhoneEveryone ? "HOST ONLY" : "EVERYONE", UiTheme.ButtonGhost, GUILayout.Width(110f)))
        {
            Coop.SetPhoneEveryone(!Coop.PhoneEveryone);
        }
        GUILayout.EndHorizontal();
        if (Coop.PhoneUser >= 0)
        {
            GUILayout.Label($"{Social.ColoredName(Coop.PhoneUser)} is on the phone", UiTheme.Small);
        }

        GUILayout.Label("DISCORD", UiTheme.Section);
        GUILayout.BeginHorizontal();
        GUILayout.Label(DiscordPresence.Enabled
            ? $"Status shown · {UiTheme.Colored(DiscordPresence.Status, UiTheme.Muted)}"
            : UiTheme.Colored("Status hidden", UiTheme.Muted), UiTheme.Label);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(DiscordPresence.Enabled ? "TURN OFF" : "TURN ON", UiTheme.ButtonGhost, GUILayout.Width(110f)))
        {
            DiscordPresence.SetEnabled(!DiscordPresence.Enabled);
            if (Plugin.DiscordSetting != null)
            {
                Plugin.DiscordSetting.Value = DiscordPresence.Enabled;
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("UPDATES", UiTheme.Section);
        GUILayout.BeginHorizontal();
        GUILayout.Label(AutoUpdate.Enabled.Value
            ? $"Automatic{(string.IsNullOrEmpty(AutoUpdate.Status) ? "" : " · " + UiTheme.Colored(UiTheme.Plain(AutoUpdate.Status), UiTheme.Muted))}"
            : UiTheme.Colored("Off", UiTheme.Muted), UiTheme.Label);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(AutoUpdate.Enabled.Value ? "TURN OFF" : "TURN ON", UiTheme.ButtonGhost, GUILayout.Width(110f)))
        {
            AutoUpdate.Enabled.Value = !AutoUpdate.Enabled.Value;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(12f);
        string again = Coop.Role == Role.Host && Coop.Players.Count > 0 ? "ENDS CO-OP FOR ALL – CLICK AGAIN" : "REALLY? CLICK AGAIN";
        if (GUILayout.Button(_confirmMainMenu ? again : "MAIN MENU", UiTheme.ButtonGhost))
        {
            if (_confirmMainMenu)
            {
                SetOpen(false);
                SteamSession.ToMainMenu();
            }
            _confirmMainMenu = !_confirmMainMenu;
        }

        GUILayout.Space(8f);
        GUILayout.Label("T: CHAT    F8 / ESC: CLOSE", UiTheme.Small);
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private static void PlayerRow(int player)
    {
        string tags = "";
        if (player == Coop.HostPlayer)
        {
            tags += UiTheme.Colored("  HOST", UiTheme.Orange);
        }
        if (player == Coop.Self)
        {
            tags += UiTheme.Colored("  YOU", UiTheme.Muted);
        }
        if (player == Coop.MachineOwner)
        {
            tags += UiTheme.Colored("  AT THE MACHINE", UiTheme.Green);
        }
        if (player == Coop.SurgeryUser)
        {
            tags += UiTheme.Colored("  AT THE SURGERY MACHINE", UiTheme.Green);
        }
        if (Coop.Role == Role.Host && player != Coop.HostPlayer && !JoinSync.InRoom.Contains(player))
        {
            tags += UiTheme.Colored("  LOADING ...", UiTheme.Muted);
        }
        if (Coop.Interrupted.Contains(player))
        {
            tags += UiTheme.Colored("  RECONNECTING ...", UiTheme.Orange);
        }
        else if (Coop.PingText(player) != null)
        {
            tags += UiTheme.Colored("  " + Coop.PingText(player), UiTheme.Muted);
        }
        GUILayout.Label($"{UiTheme.Colored("■", Avatars.ColorOf(player))}  {UiTheme.Plain(Social.NameOf(player))}{tags}", UiTheme.Label);
    }
}
