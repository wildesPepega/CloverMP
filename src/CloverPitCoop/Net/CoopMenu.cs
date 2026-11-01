using System.Collections.Generic;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace CloverPitCoop.Net;

// F8: the co-op panel (IMGUI, independent of the game's input system). Movable by its title bar.
//
// All positions are worked out by hand on every event, without GUILayout. GUILayout remembers its
// layout between events and throws when what is drawn changes in between (a player joining, the
// menu opening on a key press), which leaves the panel broken.
internal static class CoopMenu
{
    private const float Width = 460f;
    private const float Margin = 18f;
    private const float CardPadding = 12f;
    private const float HeaderHeight = 50f;
    private const float RowHeight = 28f;
    private const float SwitchRowHeight = 48f;
    private const float ButtonHeight = 36f;

    private static bool _open;
    private static bool _confirmMainMenu;
    private static bool _dragging;
    private static Vector2 _dragOffset;
    private static ConfigEntry<float> _posX;
    private static ConfigEntry<float> _posY;

    // Layout run: once to measure, once to draw.
    private static bool _draw;
    private static float _x, _y, _w;
    private static int _card;
    private static bool _inCard;
    private static readonly List<float> CardHeights = new List<float>();

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
        _dragging = false;
        _confirmMainMenu = false;
        InputBlock.Set(open || Social.IsTyping);
    }

    public static void OnGUI()
    {
        Event e = Event.current;
        if (_open && !InGame())
        {
            SetOpen(false);
        }
        if (e.type == EventType.KeyDown && !Social.IsTyping)
        {
            if (e.keyCode == KeyCode.F8 && !_open && !InGame())
            {
                Coop.Show("The co-op menu is available once you are in the room");
                e.Use();
                return;
            }
            if (e.keyCode == KeyCode.F8 || (_open && e.keyCode == KeyCode.Escape))
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    InputBlock.SwallowEscape();
                }
                SetOpen(!_open);
                e.Use();
                return;
            }
        }
        if (!_open)
        {
            return;
        }
        InputBlock.KeepCursorFree();
        UiTheme.Ensure();
        UiTheme.Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.35f));

        float w = UiTheme.Px(Width);
        float m = UiTheme.Px(Margin);
        _draw = false;
        CardHeights.Clear();
        float h = Content(0f, w - 2f * m) + m;

        Rect panel = PanelRect(w, h);
        Rect header = new Rect(panel.x, panel.y, panel.width, UiTheme.Px(HeaderHeight));
        Rect close = new Rect(panel.xMax - m - UiTheme.Px(30f), panel.y + UiTheme.Px(10f), UiTheme.Px(30f), UiTheme.Px(30f));
        HandleDrag(e, header, close, w, h);
        panel = PanelRect(w, h);
        close.x = panel.xMax - m - UiTheme.Px(30f);
        close.y = panel.y + UiTheme.Px(10f);

        GUI.Box(panel, GUIContent.none, UiTheme.Panel);
        GUI.Label(new Rect(panel.x + m, panel.y, w - 2f * m, UiTheme.Px(HeaderHeight)),
            UiTheme.Colored("♣ ", UiTheme.Accent) + "CloverMP  " + "<size=" + (int)UiTheme.Px(13f) + ">" + UiTheme.Colored("v" + Plugin.Version, UiTheme.Muted) + "</size>", UiTheme.Title);
        if (GUI.Button(close, "✕", UiTheme.ButtonIcon))
        {
            SetOpen(false);
            return;
        }

        _draw = true;
        _card = 0;
        _x = panel.x + m;
        Content(panel.y, w - 2f * m);
    }

    private static Rect PanelRect(float w, float h)
    {
        LoadPosition();
        float x = _posX.Value < 0f ? UiTheme.Px(36f) : _posX.Value;
        float y = _posY.Value < 0f ? (Screen.height - h) / 2f : _posY.Value;
        // Always reachable, also after the window got smaller.
        return new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - w)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - h)), w, h);
    }

    private static void LoadPosition()
    {
        if (_posX == null)
        {
            _posX = Plugin.Instance.Config.Bind("Menu", "PositionX", -1f, "Where the F8 menu was left (-1: default).");
            _posY = Plugin.Instance.Config.Bind("Menu", "PositionY", -1f, "Where the F8 menu was left (-1: default).");
        }
    }

    private static void HandleDrag(Event e, Rect header, Rect close, float w, float h)
    {
        if (e.type == EventType.MouseDown && e.button == 0 && header.Contains(e.mousePosition) && !close.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragOffset = e.mousePosition - header.position;
            e.Use();
        }
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            Vector2 at = e.mousePosition - _dragOffset;
            // Not written to the file on every pixel: the entries save themselves when set.
            _posX.ConfigFile.SaveOnConfigSet = false;
            _posX.Value = Mathf.Clamp(at.x, 0f, Mathf.Max(0f, Screen.width - w));
            _posY.Value = Mathf.Clamp(at.y, 0f, Mathf.Max(0f, Screen.height - h));
            _posX.ConfigFile.SaveOnConfigSet = true;
            e.Use();
        }
        else if (_dragging && e.rawType == EventType.MouseUp)
        {
            _dragging = false;
            _posX.ConfigFile.Save();
            e.Use();
        }
    }

    // Runs down the panel; returns the height. Nothing is drawn or clicked in the measuring run.
    private static float Content(float top, float width)
    {
        _w = width;
        _y = top + UiTheme.Px(HeaderHeight);
        if (!string.IsNullOrEmpty(SteamSession.Status))
        {
            Text(UiTheme.Plain(SteamSession.Status), UiTheme.Small);
            Gap(8f);
        }

        if (!SteamClient.IsValid)
        {
            Section("SESSION");
            BeginCard();
            Row(UiTheme.Colored("Steam is not connected", UiTheme.Red), null);
            EndCard();
        }
        else if (Coop.Role == Role.Off)
        {
            SessionAlone();
        }
        else
        {
            SessionPlayers();
            SessionGame();
        }
        Settings();
        Footer();
        return _y - top;
    }

    private static void SessionAlone()
    {
        Section("SESSION");
        BeginCard();
        Row("Playing alone", null);
        Gap(4f);
        Text("To join a friend, accept their Steam invite once you are in the room.", UiTheme.Small);
        Gap(8f);
        if (FullButton("Host a session", UiTheme.Button))
        {
            SteamSession.Host();
        }
        EndCard();
    }

    private static void SessionPlayers()
    {
        List<int> players = Social.AllPlayers();
        Section($"PLAYERS  {players.Count} / {SteamSession.MaxPlayers}");
        BeginCard();
        foreach (int p in players)
        {
            if (_draw)
            {
                PlayerLine(new Rect(_x + UiTheme.Px(CardPadding), _y, _w - 2f * UiTheme.Px(CardPadding), UiTheme.Px(RowHeight)), p);
            }
            _y += UiTheme.Px(RowHeight);
        }
        Gap(8f);
        bool host = Coop.Role == Role.Host;
        float pad = UiTheme.Px(CardPadding);
        float full = _w - 2f * pad;
        float gap = UiTheme.Px(8f);
        float bw = host ? (full - gap) / 2f : full;
        if (host && Btn(new Rect(_x + pad, _y, bw, UiTheme.Px(ButtonHeight)), "Invite friends", UiTheme.Button))
        {
            SteamSession.Invite();
        }
        if (Btn(new Rect(_x + pad + (host ? bw + gap : 0f), _y, bw, UiTheme.Px(ButtonHeight)), "Leave session", UiTheme.ButtonGhost))
        {
            SteamSession.Leave();
        }
        _y += UiTheme.Px(ButtonHeight);
        EndCard();
    }

    private static void SessionGame()
    {
        Section("IN THE ROOM");
        BeginCard();
        // Read here, only ever with a loaded run: the menu is only open in the room.
        Row("Slot machine", Coop.MachineOwner >= 0
            ? $"{Social.ColoredName(Coop.MachineOwner)} · {GameplayData.SpinsLeftGet()} spins · {UiTheme.Coins(GameplayData.CoinsGet())}"
            : UiTheme.Colored("Free", UiTheme.Accent));
        Row("Phone", Coop.PhoneUser >= 0 ? Social.ColoredName(Coop.PhoneUser) : UiTheme.Colored("Free", UiTheme.Accent));
        if (Coop.SurgeryUser >= 0)
        {
            Row("Surgery machine", Social.ColoredName(Coop.SurgeryUser));
        }
        EndCard();
    }

    private static void Settings()
    {
        Section("SETTINGS");
        BeginCard();
        bool canSetPhone = Coop.Role != Role.Client;
        bool everyone = Coop.PhoneEveryone;
        if (SwitchRow("Everyone can use the phone", canSetPhone ? "Off: only the host" : "Set by the host", everyone, "phone", canSetPhone))
        {
            Coop.SetPhoneEveryone(!everyone);
        }
        bool discord = DiscordPresence.Enabled;
        if (SwitchRow("Discord status", discord ? DiscordPresence.Status : "Hidden", discord, "discord", true))
        {
            DiscordPresence.SetEnabled(!discord);
            if (Plugin.DiscordSetting != null)
            {
                Plugin.DiscordSetting.Value = DiscordPresence.Enabled;
            }
        }
        bool updates = AutoUpdate.Enabled.Value;
        if (SwitchRow("Automatic updates", updates ? (string.IsNullOrEmpty(AutoUpdate.Status) ? "When the game starts" : AutoUpdate.Status) : "Off", updates, "updates", true))
        {
            AutoUpdate.Enabled.Value = !updates;
        }
        EndCard();
    }

    private static void Footer()
    {
        Gap(12f);
        string again = Coop.Role == Role.Host && Coop.Players.Count > 0 ? "Ends the session for everyone – click again" : "Click again to confirm";
        if (Btn(new Rect(_x, _y, _w, UiTheme.Px(ButtonHeight)), _confirmMainMenu ? again : "Main menu", _confirmMainMenu ? UiTheme.ButtonDangerArmed : UiTheme.ButtonDanger))
        {
            if (_confirmMainMenu)
            {
                SetOpen(false);
                SteamSession.ToMainMenu();
            }
            else
            {
                _confirmMainMenu = true;
            }
        }
        _y += UiTheme.Px(ButtonHeight);
        Gap(10f);
        if (_draw)
        {
            GUI.Label(new Rect(_x, _y, _w, UiTheme.Px(18f)), "F8  close     T  chat     Tab  players", UiTheme.SmallCenter);
        }
        _y += UiTheme.Px(18f);
    }

    // One player: colored dot, name, what they are doing, ping on the right. Also used by the tab list.
    public static void PlayerLine(Rect r, int player)
    {
        float dot = UiTheme.Px(9f);
        Color color = GUI.color;
        GUI.color = Avatars.ColorOf(player);
        GUI.DrawTexture(new Rect(r.x, r.y + (r.height - dot) / 2f, dot, dot), UiTheme.Knob);
        GUI.color = color;
        float pingWidth = UiTheme.Px(90f);
        float nameX = r.x + dot + UiTheme.Px(8f);
        GUI.Label(new Rect(nameX, r.y, r.xMax - nameX - pingWidth, r.height), UiTheme.Plain(Social.NameOf(player)) + PlayerTags(player), UiTheme.Label);
        GUI.Label(new Rect(r.xMax - pingWidth, r.y, pingWidth, r.height), PlayerPing(player), UiTheme.Value);
    }

    private static string PlayerTags(int player)
    {
        string tags = "";
        if (player == Coop.HostPlayer)
        {
            tags += "  " + UiTheme.Colored("Host", UiTheme.Warn);
        }
        if (player == Coop.Self)
        {
            tags += "  " + UiTheme.Colored("you", UiTheme.Muted);
        }
        if (player == Coop.MachineOwner)
        {
            tags += "  " + UiTheme.Colored("at the machine", UiTheme.Accent);
        }
        if (player == Coop.SurgeryUser)
        {
            tags += "  " + UiTheme.Colored("at surgery", UiTheme.Accent);
        }
        if (player == Coop.PhoneUser)
        {
            tags += "  " + UiTheme.Colored("on the phone", UiTheme.Accent);
        }
        return tags;
    }

    private static string PlayerPing(int player)
    {
        if (Coop.Interrupted.Contains(player))
        {
            return UiTheme.Colored("reconnecting", UiTheme.Warn);
        }
        if (Coop.Role == Role.Host && player != Coop.HostPlayer && !JoinSync.InRoom.Contains(player))
        {
            return UiTheme.Colored("loading", UiTheme.Muted);
        }
        if (player == Coop.HostPlayer)
        {
            return "";
        }
        int ms = Coop.PingMs(player);
        return ms < 0 ? UiTheme.Colored("–", UiTheme.Muted) : UiTheme.Colored(ms + " ms", UiTheme.PingColor(ms));
    }

    // ---- layout helpers ----

    private static void Gap(float n) => _y += UiTheme.Px(n);

    private static void Section(string title)
    {
        Gap(12f);
        if (_draw)
        {
            GUI.Label(new Rect(_x, _y, _w, UiTheme.Px(18f)), title, UiTheme.Section);
        }
        _y += UiTheme.Px(22f);
    }

    private static void BeginCard()
    {
        if (_draw)
        {
            GUI.Box(new Rect(_x, _y, _w, _card < CardHeights.Count ? CardHeights[_card] : 0f), GUIContent.none, UiTheme.Card);
        }
        else
        {
            CardHeights.Add(_y);
        }
        _inCard = true;
        Gap(CardPadding * 0.75f);
    }

    private static void EndCard()
    {
        _inCard = false;
        Gap(CardPadding * 0.75f);
        if (_draw)
        {
            _card++;
        }
        else
        {
            int last = CardHeights.Count - 1;
            CardHeights[last] = _y - CardHeights[last];
        }
    }

    private static void Text(string text, GUIStyle style)
    {
        float inset = _inCard ? UiTheme.Px(CardPadding) : 0f;
        float width = _w - 2f * inset;
        float h = style.CalcHeight(new GUIContent(text), width);
        if (_draw)
        {
            GUI.Label(new Rect(_x + inset, _y, width, h), text, style);
        }
        _y += h;
    }

    private static void Row(string left, string right)
    {
        float pad = UiTheme.Px(CardPadding);
        if (_draw)
        {
            float h = UiTheme.Px(RowHeight);
            GUI.Label(new Rect(_x + pad, _y, _w - 2f * pad, h), right == null ? left : UiTheme.Colored(left, UiTheme.Muted), UiTheme.Label);
            if (right != null)
            {
                GUI.Label(new Rect(_x + pad, _y, _w - 2f * pad, h), right, UiTheme.Value);
            }
        }
        _y += UiTheme.Px(RowHeight);
    }

    private static bool SwitchRow(string title, string detail, bool on, string id, bool enabled)
    {
        float pad = UiTheme.Px(CardPadding);
        float h = UiTheme.Px(SwitchRowHeight);
        bool clicked = false;
        if (_draw)
        {
            float sw = UiTheme.Px(40f);
            float sh = UiTheme.Px(22f);
            float textW = _w - 2f * pad - sw - UiTheme.Px(10f);
            GUI.Label(new Rect(_x + pad, _y + UiTheme.Px(4f), textW, UiTheme.Px(22f)), title, UiTheme.Label);
            GUI.Label(new Rect(_x + pad, _y + UiTheme.Px(25f), textW, UiTheme.Px(18f)), UiTheme.Plain(detail), UiTheme.SmallLine);
            GUI.enabled = enabled;
            clicked = UiTheme.Switch(new Rect(_x + _w - pad - sw, _y + (h - sh) / 2f, sw, sh), on, id);
            GUI.enabled = true;
        }
        _y += h;
        return clicked;
    }

    private static bool FullButton(string text, GUIStyle style)
    {
        float pad = UiTheme.Px(CardPadding);
        bool clicked = Btn(new Rect(_x + pad, _y, _w - 2f * pad, UiTheme.Px(ButtonHeight)), text, style);
        _y += UiTheme.Px(ButtonHeight);
        return clicked;
    }

    private static bool Btn(Rect r, string text, GUIStyle style) => _draw && GUI.Button(r, text, style);
}

// Hold Tab: who is in the session, with their ping.
internal static class TabList
{
    private const float Width = 520f;
    private const float RowHeight = 30f;

    private static bool _held;

    public static void OnGUI()
    {
        Event e = Event.current;
        // Shift+Tab is the Steam overlay.
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Tab && !e.shift && !Social.IsTyping && !CoopMenu.IsOpen)
        {
            _held = true;
            e.Use();
        }
        else if (e.rawType == EventType.KeyUp && e.keyCode == KeyCode.Tab)
        {
            _held = false;
        }
        // The key-up never arrives when the window loses focus while Tab is down.
        if (!Application.isFocused)
        {
            _held = false;
        }
        if (!_held || e.type != EventType.Repaint || Coop.Role == Role.Off || CoopMenu.IsOpen || Social.IsTyping || !CoopMenu.InGame())
        {
            return;
        }
        UiTheme.Ensure();
        List<int> players = Social.AllPlayers();
        float w = UiTheme.Px(Width);
        float pad = UiTheme.Px(18f);
        float header = UiTheme.Px(44f);
        float row = UiTheme.Px(RowHeight);
        var panel = new Rect((Screen.width - w) / 2f, Screen.height * 0.14f, w, header + players.Count * row + UiTheme.Px(14f));
        GUI.Box(panel, GUIContent.none, UiTheme.Panel);
        GUI.Label(new Rect(panel.x + pad, panel.y, w - 2f * pad, header), "Players", UiTheme.LabelBold);
        GUI.Label(new Rect(panel.x + pad, panel.y, w - 2f * pad, header), UiTheme.Colored($"{players.Count} / {SteamSession.MaxPlayers}", UiTheme.Muted), UiTheme.Value);
        UiTheme.Fill(new Rect(panel.x + pad, panel.y + header - 1f, w - 2f * pad, 1f), new Color(1f, 1f, 1f, 0.08f));
        float y = panel.y + header + UiTheme.Px(4f);
        foreach (int p in players)
        {
            CoopMenu.PlayerLine(new Rect(panel.x + pad, y, w - 2f * pad, row), p);
            y += row;
        }
    }
}
