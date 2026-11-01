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

