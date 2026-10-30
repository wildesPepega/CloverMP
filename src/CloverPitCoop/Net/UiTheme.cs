using UnityEngine;

namespace CloverPitCoop.Net;

// Look of the mod's own screens (F8 panel, chat, notices): dark translucent panels with a thin
// gold frame, gold buttons, clover-green accents. Plain IMGUI with generated textures, no assets.
internal static class UiTheme
{
    public static readonly Color Gold = new Color(0.93f, 0.74f, 0.27f);
    public static readonly Color Green = new Color(0.42f, 0.86f, 0.45f);
    public static readonly Color Text = new Color(0.94f, 0.92f, 0.86f);
    public static readonly Color Muted = new Color(0.62f, 0.6f, 0.55f);
    public static readonly Color Red = new Color(0.95f, 0.38f, 0.35f);

    public static GUIStyle Panel, PanelSoft, Title, Small, Label, Section, Button, ButtonGhost, Input, Toast, ChatLine;

    private static bool _ready;

    public static void Ensure()
    {
        if (_ready)
        {
            return;
        }
        _ready = true;
        Texture2D panel = Framed(new Color(0.05f, 0.06f, 0.05f, 0.93f), new Color(Gold.r, Gold.g, Gold.b, 0.9f));
        Texture2D soft = Solid(new Color(0.03f, 0.04f, 0.03f, 0.62f));
        Texture2D gold = Framed(Gold, new Color(1f, 0.9f, 0.55f));
        Texture2D goldHover = Framed(new Color(1f, 0.84f, 0.38f), new Color(1f, 0.95f, 0.7f));
        Texture2D goldDown = Framed(new Color(0.75f, 0.58f, 0.18f), Gold);
        Texture2D ghost = Framed(new Color(0.1f, 0.11f, 0.09f, 0.95f), new Color(Gold.r, Gold.g, Gold.b, 0.7f));
        Texture2D ghostHover = Framed(new Color(0.16f, 0.17f, 0.13f, 0.95f), Gold);
        Texture2D input = Framed(new Color(0.02f, 0.02f, 0.02f, 0.95f), new Color(Green.r, Green.g, Green.b, 0.8f));

        Panel = new GUIStyle { normal = { background = panel }, border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(18, 18, 14, 16) };
        PanelSoft = new GUIStyle { normal = { background = soft }, padding = new RectOffset(12, 12, 8, 8) };
        Title = new GUIStyle { fontSize = 24, fontStyle = FontStyle.Bold, normal = { textColor = Gold }, richText = true };
        Small = new GUIStyle { fontSize = 13, normal = { textColor = Muted }, richText = true, wordWrap = true };
        Label = new GUIStyle { fontSize = 16, normal = { textColor = Text }, richText = true, wordWrap = true, margin = new RectOffset(0, 0, 3, 3) };
        Section = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Green }, margin = new RectOffset(0, 0, 12, 4) };
        Button = new GUIStyle
        {
            fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, border = new RectOffset(2, 2, 2, 2),
            normal = { background = gold, textColor = new Color(0.12f, 0.09f, 0.03f) },
            hover = { background = goldHover, textColor = new Color(0.12f, 0.09f, 0.03f) },
            active = { background = goldDown, textColor = new Color(0.12f, 0.09f, 0.03f) },
            margin = new RectOffset(0, 0, 6, 4), fixedHeight = 38f,
        };
        ButtonGhost = new GUIStyle(Button)
        {
            fontSize = 15,
            normal = { background = ghost, textColor = Gold },
            hover = { background = ghostHover, textColor = new Color(1f, 0.9f, 0.6f) },
            active = { background = ghost, textColor = Gold },
            fixedHeight = 32f,
        };
        Input = new GUIStyle
        {
            fontSize = 17, normal = { background = input, textColor = Text }, focused = { background = input, textColor = Text },
            border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(10, 10, 7, 7), clipping = TextClipping.Clip,
        };
        Toast = new GUIStyle(Panel) { fontSize = 18, alignment = TextAnchor.MiddleCenter, normal = { background = panel, textColor = Text }, wordWrap = true, richText = true, padding = new RectOffset(18, 18, 10, 10) };
        ChatLine = new GUIStyle { fontSize = 16, normal = { textColor = Text }, richText = true, wordWrap = true, padding = new RectOffset(0, 0, 1, 1) };
    }

    public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

    public static string Colored(string text, Color c) => $"<color=#{Hex(c)}>{text}</color>";

    // IMGUI rich text: keep players from injecting tags into names or chat.
    public static string Plain(string s) => s?.Replace("<", "‹").Replace(">", "›") ?? "";

    private static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    // 6x6 texture: 1 px frame, filled center; drawn 9-sliced through the style's border.
    private static Texture2D Framed(Color fill, Color frame)
    {
        var t = new Texture2D(6, 6) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
        for (int x = 0; x < 6; x++)
        {
            for (int y = 0; y < 6; y++)
            {
                bool edge = x == 0 || y == 0 || x == 5 || y == 5;
                t.SetPixel(x, y, edge ? frame : fill);
            }
        }
        t.Apply();
        return t;
    }
}
