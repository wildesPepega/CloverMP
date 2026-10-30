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

    private static float _nextGaze;
    private static bool _typing;
    private static int _openedFrame = -10;
    private static string _draft = "";
    private static GUIStyle _tag, _chat;

    public static string MyName => SteamClient.IsValid ? SteamClient.Name : "Player";

    public static string NameOf(int player)
    {
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
                    Coop.Link.Send($"G|{player}|{p[2]}|{p[3]}|{p[4]}|{p[5]}|{p[6]}");
                }
                if (player != Coop.Self)
                {
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
        Coop.Link.Send($"G|{Coop.Self}|{S(head.x)}|{S(head.y)}|{S(head.z)}|{S(e.y)}|{S(e.x)}");
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
        _tag ??= new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        _chat ??= new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true };
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
            if (Time.unscaledTime - GazeSeen[g.Key] > 1.5f)
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
            var r = new Rect(x - 120f, y - 30f, 240f, 26f);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), NameOf(g.Key), _tag);
            GUI.color = Avatars.ColorOf(g.Key);
            GUI.Label(r, NameOf(g.Key), _tag);
            GUI.color = Color.white;
        }
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
        const float width = 560f;
        const float lineH = 24f;
        float x = 24f;
        float bottom = Screen.height - 24f - (_typing ? 46f : 0f);

        var visible = new List<(float alpha, string text)>();
        foreach ((float time, string line) in Chat)
        {
            float age = Time.unscaledTime - time;
            float alpha = _typing ? 1f : Mathf.Clamp01((ChatShowSeconds - age) / 2f);
            if (alpha > 0f)
            {
                visible.Add((alpha, line));
            }
        }
        if (visible.Count > 0 || _typing)
        {
            float h = Mathf.Max(1, visible.Count) * lineH + 16f;
            float top = bottom - h;
            float panelAlpha = 0f;
            foreach ((float alpha, string _) in visible)
            {
                panelAlpha = Mathf.Max(panelAlpha, alpha);
            }
            GUI.color = new Color(1f, 1f, 1f, _typing ? 1f : panelAlpha);
            GUI.Box(new Rect(x - 10f, top, width + 20f, h), GUIContent.none, UiTheme.PanelSoft);
            float y = top + 8f;
            foreach ((float alpha, string text) in visible)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(x, y, width, lineH), text, UiTheme.ChatLine);
                y += lineH;
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
        var inputRect = new Rect(x - 10f, Screen.height - 62f, width + 20f, 38f);
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
            GUI.Label(new Rect(inputRect.x + 12f, inputRect.y + 9f, width, 24f), "Message … (Enter to send, Esc to cancel)", UiTheme.Small);
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
