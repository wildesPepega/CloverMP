using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverPitCoop.Net;

// Look of the mod's own screens (F8 panel, tab list, chat, notices): dark rounded panels, a clean
// system font, one green accent. Plain IMGUI with generated textures, no assets of its own.
// Everything scales with the window height, so it reads the same on 1080p and 4K.
internal static class UiTheme
{
    public static readonly Color Accent = new Color(0.2f, 0.83f, 0.6f);
    public static readonly Color Text = new Color(0.93f, 0.94f, 0.95f);
    public static readonly Color Muted = new Color(0.6f, 0.63f, 0.68f);
    public static readonly Color Warn = new Color(0.98f, 0.75f, 0.25f);
    public static readonly Color Red = new Color(0.97f, 0.44f, 0.44f);

    private static readonly Color Background = new Color(0.07f, 0.08f, 0.1f, 0.98f);
    private static readonly Color Surface = new Color(0.12f, 0.13f, 0.16f, 1f);
    private static readonly Color SurfaceHover = new Color(0.18f, 0.2f, 0.24f, 1f);
    private static readonly Color Line = new Color(0.22f, 0.24f, 0.28f, 1f);
    private static readonly Color SwitchOff = new Color(0.27f, 0.29f, 0.34f, 1f);

    public static GUIStyle Panel, PanelSoft, Card, Title, Small, SmallLine, SmallCenter, Label, LabelBold, Value, Section, Button, ButtonGhost, ButtonDanger, ButtonDangerArmed,
        ButtonIcon, Input, Toast, ChatLine, NameTag, TrackOn, TrackOff, HitArea;

    public static Texture2D Knob;

    public static float Scale { get; private set; } = 1f;

    private static bool _texturesReady;
    private static Texture2D _panel, _panelSoft, _card, _ghost, _ghostHover, _accent, _accentHover, _accentDown, _dangerHover, _red, _input, _trackOn, _trackOff, _toast, _clear;
    private static Font _font;
    private static readonly Dictionary<string, float> Switches = new Dictionary<string, float>();

    public static float Px(float n) => Mathf.Round(n * Scale);

    public static void Ensure()
    {
        float scale = Mathf.Clamp(Screen.height / 1080f, 1f, 2.5f);
        if (Panel != null && Mathf.Abs(scale - Scale) < 0.01f)
        {
            return;
        }
        Scale = scale;
        if (!_texturesReady)
        {
            _texturesReady = true;
            MakeTextures();
            _font = SystemFont();
        }
        Build();
    }

    private static void MakeTextures()
    {
        _panel = Rounded(Background, Line, 12);
        _panelSoft = Rounded(new Color(0.05f, 0.06f, 0.08f, 0.8f), new Color(0f, 0f, 0f, 0f), 10);
        _card = Rounded(Surface, Surface, 9);
        _ghost = Rounded(Surface, Line, 8);
        _ghostHover = Rounded(SurfaceHover, new Color(0.34f, 0.37f, 0.42f), 8);
        _accent = Rounded(Accent, Accent, 8);
        _accentHover = Rounded(new Color(0.33f, 0.9f, 0.69f), new Color(0.33f, 0.9f, 0.69f), 8);
        _accentDown = Rounded(new Color(0.14f, 0.68f, 0.49f), new Color(0.14f, 0.68f, 0.49f), 8);
        _dangerHover = Rounded(new Color(0.3f, 0.13f, 0.14f), Red, 8);
        _red = Rounded(Red, Red, 8);
        _input = Rounded(Surface, Accent, 8);
        _trackOn = Rounded(Accent, Accent, 11);
        _trackOff = Rounded(SwitchOff, SwitchOff, 11);
        _toast = Rounded(Background, Line, 10);
        _clear = Rounded(new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0f), 2);
        Knob = Rounded(Color.white, Color.white, 16);
    }

    private static void Build()
    {
        Color darkOnAccent = new Color(0.03f, 0.1f, 0.07f);
        Panel = Box(_panel, 12);
        Panel.padding = Pad(18, 18, 14, 16);
        PanelSoft = Box(_panelSoft, 10);
        Card = Box(_card, 9);
        Title = new GUIStyle { fontSize = (int)Px(21), fontStyle = FontStyle.Bold, normal = { textColor = Text }, richText = true, alignment = TextAnchor.MiddleLeft };
        Small = new GUIStyle { fontSize = (int)Px(13), normal = { textColor = Muted }, richText = true, wordWrap = true };
        Label = new GUIStyle { fontSize = (int)Px(15), normal = { textColor = Text }, richText = true, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        Value = new GUIStyle(Label) { alignment = TextAnchor.MiddleRight };
        SmallLine = new GUIStyle(Small) { wordWrap = false, clipping = TextClipping.Clip };
        SmallCenter = new GUIStyle(Small) { alignment = TextAnchor.MiddleCenter };
        LabelBold = new GUIStyle(Label) { fontStyle = FontStyle.Bold };
        Section = new GUIStyle { fontSize = (int)Px(12), fontStyle = FontStyle.Bold, normal = { textColor = Muted }, alignment = TextAnchor.MiddleLeft };
        Button = ButtonStyle(_accent, _accentHover, _accentDown, darkOnAccent, darkOnAccent);
        Button.fontStyle = FontStyle.Bold;
        ButtonGhost = ButtonStyle(_ghost, _ghostHover, _ghost, Text, Text);
        ButtonDanger = ButtonStyle(_ghost, _dangerHover, _dangerHover, Red, Red);
        ButtonDangerArmed = ButtonStyle(_red, _red, _dangerHover, Color.black, Color.black);
        ButtonDangerArmed.fontStyle = FontStyle.Bold;
        ButtonIcon = ButtonStyle(_clear, _ghostHover, _ghost, Muted, Text);
        Input = new GUIStyle
        {
            fontSize = (int)Px(15), normal = { background = _input, textColor = Text }, focused = { background = _input, textColor = Text },
            hover = { background = _input, textColor = Text }, active = { background = _input, textColor = Text },
            border = Border(8), padding = Pad(10, 10, 9, 7), clipping = TextClipping.Clip,
        };
        Toast = Box(_toast, 10);
        Toast.fontSize = (int)Px(15);
        Toast.alignment = TextAnchor.MiddleCenter;
        Toast.normal.textColor = Text;
        Toast.wordWrap = true;
        Toast.richText = true;
        Toast.padding = Pad(16, 16, 8, 8);
        ChatLine = new GUIStyle { fontSize = (int)Px(15), normal = { textColor = Text }, richText = true, wordWrap = true, padding = new RectOffset(0, 0, 1, 1) };
        NameTag = new GUIStyle { fontSize = (int)Px(15), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        TrackOn = Box(_trackOn, 11);
        TrackOff = Box(_trackOff, 11);
        HitArea = new GUIStyle();
        foreach (GUIStyle s in new[] { Title, Small, SmallLine, SmallCenter, Label, LabelBold, Value, Section, Button, ButtonGhost, ButtonDanger, ButtonDangerArmed, ButtonIcon, Input, Toast, ChatLine, NameTag })
        {
            s.font = _font;
        }
    }

    private static GUIStyle ButtonStyle(Texture2D normal, Texture2D hover, Texture2D down, Color text, Color hoverText)
    {
        return new GUIStyle
        {
            fontSize = (int)Px(14), alignment = TextAnchor.MiddleCenter, border = Border(8),
            normal = { background = normal, textColor = text },
            hover = { background = hover, textColor = hoverText },
            active = { background = down, textColor = hoverText },
        };
    }

    private static GUIStyle Box(Texture2D t, int border) => new GUIStyle { normal = { background = t }, border = Border(border) };

    private static RectOffset Border(int n) => new RectOffset(n, n, n, n);

    private static RectOffset Pad(int l, int r, int t, int b) => new RectOffset((int)Px(l), (int)Px(r), (int)Px(t), (int)Px(b));

    private static Font SystemFont()
    {
        try
        {
            return Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans" }, 16);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[coop] ui: no system font: " + e.Message);
            return null;
        }
    }

    // A switch that slides over instead of a button. True when it was clicked.
    public static bool Switch(Rect r, bool on, string id)
    {
        if (!Switches.TryGetValue(id, out float t))
        {
            t = on ? 1f : 0f;
        }
        if (Event.current.type == EventType.Repaint)
        {
            t = Mathf.MoveTowards(t, on ? 1f : 0f, Time.unscaledDeltaTime * 8f);
            Switches[id] = t;
        }
        bool enabled = GUI.enabled;
        GUI.color = new Color(1f, 1f, 1f, enabled ? 1f : 0.45f);
        GUI.Box(r, GUIContent.none, TrackOff);
        GUI.color = new Color(1f, 1f, 1f, (enabled ? 1f : 0.45f) * t);
        GUI.Box(r, GUIContent.none, TrackOn);
        float margin = Px(3f);
        float d = r.height - 2f * margin;
        GUI.color = new Color(1f, 1f, 1f, enabled ? 1f : 0.6f);
        GUI.DrawTexture(new Rect(r.x + margin + (r.width - d - 2f * margin) * t, r.y + margin, d, d), Knob);
        GUI.color = Color.white;
        return GUI.Button(r, GUIContent.none, HitArea);
    }

    public static void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

    public static string Colored(string text, Color c) => $"<color=#{Hex(c)}>{text}</color>";

    // The game's coin sign.
    public static string Coins(object amount) => amount + " " + Colored("Φ", Warn);

    public static Color PingColor(int ms) => ms < 80 ? Accent : ms < 180 ? Warn : Red;

    // IMGUI rich text: keep players from injecting tags into names or chat.
    public static string Plain(string s) => s?.Replace("<", "‹").Replace(">", "›") ?? "";

    // Filled rounded rectangle with a 1 px frame and smooth edges; drawn 9-sliced through the
    // style's border.
    private static Texture2D Rounded(Color fill, Color frame, int radius)
    {
        int size = radius * 2 + 2;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        float half = size / 2f;
        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size; y++)
            {
                // Distance to the edge of the rounded shape; negative is inside.
                float dx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                float dy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                float outside = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude;
                float dist = outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;
                float alpha = Mathf.Clamp01(0.5f - dist);
                Color c = Color.Lerp(fill, frame, Mathf.Clamp01(dist + 1.5f));
                c.a *= alpha;
                t.SetPixel(x, y, c);
            }
        }
        t.Apply();
        return t;
    }
}
