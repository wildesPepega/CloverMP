using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Panik;
using Steamworks;
using UnityEngine;

namespace CloverPitCoop.Net;

// Steam names, chat (T) and name tags at the point each teammate looks at.
// Everyone sits on the same chair in CloverPit, so a teammate's "position" is shown as the spot
// in the room they are looking at.
//
//   N|<player>|<name b64>               host -> all, player names (sent for everyone on join)
//   C|<player>|<text b64>               chat line; clients send to host, host relays to all
//   G|<player>|<x>|<y>|<z>              gaze point; clients send to host, host relays to all
internal static class Social
{
    private const float GazeInterval = 0.2f;
    private const float GazeDistance = 2.5f;
    private const int ChatKeep = 10;
    private const float ChatShowSeconds = 12f;

    public static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
    private static readonly Dictionary<int, Vector3> Gaze = new Dictionary<int, Vector3>();
    private static readonly Dictionary<int, float> GazeSeen = new Dictionary<int, float>();
    private static readonly List<(float time, string line)> Chat = new List<(float, string)>();

    // Other players who have the chat open right now; comes with their position updates.
    private static readonly HashSet<int> Typing = new HashSet<int>();

    private static float _nextGaze;
    private static bool _typing;
    private static int _openedFrame = -10;
    private static string _draft = "";
    private static GUIStyle _tag;

    public static string MyName => SteamClient.IsValid ? SteamClient.Name : "Player";

    public static string NameOf(int player)
    {
        // Known from the start: the list below only fills up once someone is connected.
        if (player == Coop.Self)
        {
            return MyName;
        }
        if (Names.TryGetValue(player, out string n) && !string.IsNullOrEmpty(n))
        {
            return n;
        }
        return player == Coop.HostPlayer ? "Host" : "Player " + player;
    }

    public static string Encode(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));

    public static string Decode(string s)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return "?";
        }
    }

    public static void Reset()
    {
        Names.Clear();
        _sentName = null;
        Gaze.Clear();
        Avatars.Clear();
        GazeSeen.Clear();
        Typing.Clear();
        Roster.Clear();
        _typing = false;
    }

    // ---- messages ----

    // Host: a player said hello with its name; tell everyone all names.
    public static void HostLearnedName(int player, string name)
    {
        Names[Coop.HostPlayer] = MyName;
        Names[player] = name;
        foreach (KeyValuePair<int, string> n in Names)
        {
            Coop.Link.Send($"N|{n.Key}|{Encode(n.Value)}");
        }
    }

    public static void Handle(int from, string[] p)
    {
        switch (p[0])
        {
            case "N":
                if (Coop.Role == Role.Host)
                {
                    // A client tells its (possibly late, once Steam is up) name: store and share.
                    HostLearnedName(from, Decode(p[2]));
                }
                else
                {
                    Names[int.Parse(p[1])] = Decode(p[2]);
                }
                break;
            case "C":
            {
                int player = Coop.Role == Role.Host ? from : int.Parse(p[1]);
                if (Coop.Role == Role.Host)
                {
                    Coop.Link.Send($"C|{player}|{p[2]}");
                }
                if (player != Coop.Self)
                {
                    AddChat($"{ColoredName(player)}: {UiTheme.Plain(Decode(p[2]))}");
                    // Quiet blip; not for own messages or info lines.
                    ChatSound.Play();
                }
                break;
            }
            case "G":
            {
                if (p.Length < 7)
                {
                    break;
                }
                int player = Coop.Role == Role.Host ? from : int.Parse(p[1]);
                if (Coop.Role == Role.Host)
                {
                    Coop.Link.SendVolatile($"G|{player}|{p[2]}|{p[3]}|{p[4]}|{p[5]}|{p[6]}|{(p.Length > 7 ? p[7] : "0")}");
                }
                if (player != Coop.Self)
                {
                    if (p.Length > 7 && p[7] == "1")
                    {
                        Typing.Add(player);
                    }
                    else
                    {
                        Typing.Remove(player);
                    }
                    Gaze[player] = new Vector3(F(p[2]), F(p[3]), F(p[4]));
                    GazeSeen[player] = Time.unscaledTime;
                    Avatars.Set(player, Gaze[player], F(p[5]), F(p[6]));
                }
                break;
            }
        }
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static string S(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

    private static void AddChat(string line)
    {
        Chat.Add((Time.unscaledTime, line));
        if (Chat.Count > ChatKeep)
        {
            Chat.RemoveAt(0);
        }
    }

    // ---- every frame ----

    private static string _sentName;

    public static void Tick()
    {
        if (Coop.Role == Role.Off || Coop.Link == null || !Coop.Link.Connected || Time.unscaledTime < _nextGaze)
        {
            return;
        }
        // The hello can go out before Steam knows our name; send it again once it changes.
        if (_sentName != MyName)
        {
            _sentName = MyName;
            if (Coop.Role == Role.Client)
            {
                Coop.Link.Send($"N|{Coop.Self}|{Encode(MyName)}");
            }
            else
            {
                Names[Coop.HostPlayer] = MyName;
                Coop.Link.Send($"N|{Coop.HostPlayer}|{Encode(MyName)}");
            }
        }
        _nextGaze = Time.unscaledTime + GazeInterval;
        // Only a player who stands in the room (past intro, title screen and run menu, not while
        // loading the host's run) shows up for the others.
        Camera cam = ViewCamera();
        if (cam == null || !CoopMenu.InGame() || JoinSync.Loading)
        {
            return;
        }
        // Head = the view camera (it follows the player when walking and sits at the machine,
        // ATM, store ... during close-ups), plus where it looks.
        Transform t = cam.transform;
        Vector3 head = t.position;
        Vector3 e = t.eulerAngles;
        // Replaced 5 times a second anyway: may get lost, and never holds up the game's actions.
        Coop.Link.SendVolatile($"G|{Coop.Self}|{S(head.x)}|{S(head.y)}|{S(head.z)}|{S(e.y)}|{S(e.x)}|{(_typing ? "1" : "0")}");
    }

    private static Camera ViewCamera()
    {
        if (CameraGame.firstInstance != null && CameraGame.firstInstance.myCamera != null)
        {
            return CameraGame.firstInstance.myCamera;
        }
        return Camera.main;
    }

    public static void OnGUI()
    {
        if (Coop.Role == Role.Off)
        {
            return;
        }
        UiTheme.Ensure();
        _tag = UiTheme.NameTag;
        DrawTags();
        DrawChat();
    }

    private static void DrawTags()
    {
        Camera cam = ViewCamera();
        if (cam == null || !CoopMenu.InGame() || JoinSync.Loading)
        {
            return;
        }
        foreach (KeyValuePair<int, Vector3> g in Gaze)
        {
            if (Time.unscaledTime - GazeSeen[g.Key] > Avatars.StaleAfter)
            {
                continue;
            }
            Vector3 s = cam.WorldToScreenPoint(Avatars.HeadOf(g.Key, g.Value) + Vector3.up * 0.35f);
            if (s.z <= 0.3f)
            {
                continue;
            }
            // Scale the camera's pixel space to the GUI screen (they differ when the game renders
            // at a lower resolution than the window).
            float x = s.x / cam.pixelWidth * Screen.width;
            float y = Screen.height - s.y / cam.pixelHeight * Screen.height;
            var r = new Rect(x - UiTheme.Px(120f), y - UiTheme.Px(30f), UiTheme.Px(240f), UiTheme.Px(26f));
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), NameOf(g.Key), _tag);
            GUI.color = Avatars.ColorOf(g.Key);
            GUI.Label(r, NameOf(g.Key), _tag);
            GUI.color = Color.white;
            if (Typing.Contains(g.Key))
            {
                DrawTypingBubble(x, r.y - 4f);
            }
        }
    }

    // Small rounded speech bubble with three dots lighting up in turn, standing on the name.
    private static void DrawTypingBubble(float centerX, float bottom)
    {
        float dot = UiTheme.Px(6f);
        float gap = UiTheme.Px(4f);
        float width = 3f * dot + 2f * gap + UiTheme.Px(20f);
        float height = UiTheme.Px(22f);
        float top = bottom - height;
        GUI.Box(new Rect(centerX - width / 2f, top, width, height), GUIContent.none, UiTheme.Toast);
        int active = (int)(Time.unscaledTime * 3f) % 3;
        for (int i = 0; i < 3; i++)
        {
            float dotX = centerX - (3f * dot + 2f * gap) / 2f + i * (dot + gap);
            GUI.color = i == active ? UiTheme.Accent : UiTheme.Muted;
            GUI.DrawTexture(new Rect(dotX, top + (height - dot) / 2f, dot, dot), UiTheme.Knob);
        }
        GUI.color = Color.white;
    }

    // Chat box bottom left: recent lines fade out after a while; while typing the whole history
    // shows on a dark panel with the input line below.
    private static void DrawChat()
    {
        Event e = Event.current;
        if (!_typing && e.type == EventType.KeyDown && e.keyCode == KeyCode.T && !CoopMenu.IsOpen)
        {
            _typing = true;
            _draft = "";
            _openedFrame = Time.frameCount;
            InputBlock.Set(true);
            e.Use();
            return;
        }
        // The T that opened the chat also arrives as a typed character (a separate KeyDown event
        // with keyCode None, same or next frame); swallow it so it does not land in the input.
        if (_typing && e.type == EventType.KeyDown && e.keyCode == KeyCode.None && (e.character == 't' || e.character == 'T')
            && Time.frameCount - _openedFrame <= 1 && _draft.Length == 0)
        {
            e.Use();
            return;
        }
        UiTheme.Ensure();
        float width = UiTheme.Px(560f);
        float lineH = UiTheme.Px(24f);
        float x = UiTheme.Px(34f);
        float bottom = Screen.height - UiTheme.Px(24f) - (_typing ? UiTheme.Px(46f) : 0f);

        var visible = new List<(float alpha, string text, float height)>();
        float linesHeight = 0f;
        foreach ((float time, string line) in Chat)
        {
            float age = Time.unscaledTime - time;
            float alpha = _typing ? 1f : Mathf.Clamp01((ChatShowSeconds - age) / 2f);
            if (alpha > 0f)
            {
                // A long message takes several lines.
                float height = Mathf.Max(lineH, UiTheme.ChatLine.CalcHeight(new GUIContent(line), width) + 4f);
                visible.Add((alpha, line, height));
                linesHeight += height;
            }
        }
        if (visible.Count > 0 || _typing)
        {
            float h = Mathf.Max(lineH, linesHeight) + UiTheme.Px(16f);
            float top = bottom - h;
            float panelAlpha = 0f;
            foreach ((float alpha, string _, float _) in visible)
            {
                panelAlpha = Mathf.Max(panelAlpha, alpha);
            }
            GUI.color = new Color(1f, 1f, 1f, _typing ? 1f : panelAlpha);
            GUI.Box(new Rect(x - UiTheme.Px(10f), top, width + UiTheme.Px(20f), h), GUIContent.none, UiTheme.PanelSoft);
            float y = top + UiTheme.Px(8f);
            foreach ((float alpha, string text, float height) in visible)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(x, y, width, height), text, UiTheme.ChatLine);
                y += height;
            }
            GUI.color = Color.white;
        }
        if (!_typing)
        {
            return;
        }
        InputBlock.KeepCursorFree();
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
        {
            Send(_draft);
            Close();
            e.Use();
            return;
        }
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            Close();
            e.Use();
            return;
        }
        var inputRect = new Rect(x - UiTheme.Px(10f), Screen.height - UiTheme.Px(62f), width + UiTheme.Px(20f), UiTheme.Px(38f));
        GUI.SetNextControlName("coop-chat");
        _draft = GUI.TextField(inputRect, _draft, 200, UiTheme.Input);
        GUI.FocusControl("coop-chat");
        // Focusing selects the whole text; put the cursor at the end instead.
        if (GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) is TextEditor te && te.selectIndex != te.cursorIndex)
        {
            te.selectIndex = te.cursorIndex = _draft.Length;
        }
        if (string.IsNullOrEmpty(_draft))
        {
            GUI.Label(new Rect(inputRect.x + UiTheme.Px(11f), inputRect.y + UiTheme.Px(10f), width, UiTheme.Px(24f)), "Message … (Enter to send, Esc to cancel)", UiTheme.Small);
        }
    }

    // Test helpers (screenshots of the chat).
    public static void SendForTest(string text) => Send(text);

    public static void OpenChatForTest(string draft)
    {
        _typing = true;
        _draft = draft;
        InputBlock.Set(true);
    }

    public static void CloseChatForTest() => Close();

    public static string ColoredName(int player) => UiTheme.Colored(UiTheme.Plain(NameOf(player)), Avatars.ColorOf(player));

    // Gray info line in the chat (joins, leaves, sync notices).
    public static void SystemLine(string text)
    {
        AddChat(UiTheme.Colored("• " + text, UiTheme.Muted));
    }

    public static bool IsTyping => _typing;

    // The host's list of who is in the session (client side); names stay known after someone left.
    private static readonly List<int> Roster = new List<int>();

    public static void SetRoster(string ids)
    {
        Roster.Clear();
        foreach (string id in ids.Split(','))
        {
            if (int.TryParse(id, out int player))
            {
                Roster.Add(player);
            }
        }
    }

    // Everyone in the session, the host first.
    public static List<int> AllPlayers()
    {
        var all = new List<int> { Coop.HostPlayer };
        all.AddRange(Coop.Role == Role.Host ? Coop.Players : Roster);
        if (!all.Contains(Coop.Self))
        {
            all.Add(Coop.Self);
        }
        return all;
    }

    public static IEnumerable<int> KnownPlayers()
    {
        foreach (int p in Names.Keys)
        {
            if (p != Coop.HostPlayer)
            {
                yield return p;
            }
        }
    }

    private static void Close()
    {
        _typing = false;
        if (!CoopMenu.IsOpen)
        {
            InputBlock.Set(false);
        }
    }

    private static void Send(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || Coop.Link == null)
        {
            return;
        }
        AddChat($"{ColoredName(Coop.Self)}: {UiTheme.Plain(text)}");
        Coop.Link.Send($"C|{Coop.Self}|{Encode(text)}");
    }
}
