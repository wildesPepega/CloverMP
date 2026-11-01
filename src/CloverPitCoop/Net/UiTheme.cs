using System;
using UnityEngine;

namespace CloverPitCoop.Net;

// Look of the mod's own screens (F8 panel, chat, notices), taken from the game's own screens:
// plain black boxes without frames, gray text in capitals, the game's orange for what matters,
// the clover's dark green. Plain IMGUI with generated textures, no assets of its own.
//
// The game draws its texts with TextMeshPro, which IMGUI cannot use. Its data also contains the
// font "Perfect DOS VGA 437"; if that one is loaded it is used here, otherwise a similar font of
// the system.
internal static class UiTheme
{
    // The game's C_ORANGE.
    public static readonly Color Orange = new Color(1f, 0.5f, 0f);
    public static readonly Color Green = new Color(0.5f, 0.72f, 0.16f);
    public static readonly Color Text = new Color(0.84f, 0.84f, 0.8f);
    public static readonly Color Muted = new Color(0.5f, 0.5f, 0.47f);
    public static readonly Color Red = new Color(1f, 0.2f, 0.15f);

    public static GUIStyle Panel, PanelSoft, Title, Small, Label, Section, Button, ButtonGhost, Input, Toast, ChatLine, NameTag;

    private const int MaxFontLooks = 12;
    private static bool _ready;
    private static bool _gameFont;
    private static int _fontLooks;
    private static float _nextFontLook;

    public static void Ensure()
    {
        if (!_ready)
        {
            _ready = true;
            Build();
            ApplyFont(SystemFont());
        }
        // The game may load its fonts only with a later scene.
        if (!_gameFont && _fontLooks < MaxFontLooks && Time.unscaledTime >= _nextFontLook)
        {
            _nextFontLook = Time.unscaledTime + 5f;
            LookForGameFont();
        }
    }

    private static void Build()
    {
        Color black = new Color(0f, 0f, 0f, 0.96f);
        Color dimFrame = new Color(0.32f, 0.32f, 0.3f);
        Texture2D panel = Solid(black);
        Texture2D soft = Solid(new Color(0f, 0f, 0f, 0.78f));
        Texture2D framedOrange = Framed(black, Orange);
        Texture2D filledOrange = Framed(Orange, Orange);
        Texture2D pressedOrange = Framed(new Color(0.7f, 0.35f, 0f), Orange);
        Texture2D framedDim = Framed(black, dimFrame);

        Panel = new GUIStyle { normal = { background = panel }, padding = new RectOffset(18, 18, 14, 16) };
        PanelSoft = new GUIStyle { normal = { background = soft }, padding = new RectOffset(12, 12, 8, 8) };
        Title = new GUIStyle { fontSize = 32, normal = { textColor = Text }, richText = true };
        Small = new GUIStyle { fontSize = 16, normal = { textColor = Muted }, richText = true, wordWrap = true };
        Label = new GUIStyle { fontSize = 16, normal = { textColor = Text }, richText = true, wordWrap = true, margin = new RectOffset(0, 0, 3, 3) };
        Section = new GUIStyle { fontSize = 16, normal = { textColor = Orange }, margin = new RectOffset(0, 0, 14, 4) };
        Button = new GUIStyle
        {
            fontSize = 16, alignment = TextAnchor.MiddleCenter, border = new RectOffset(2, 2, 2, 2),
            normal = { background = framedOrange, textColor = Orange },
            hover = { background = filledOrange, textColor = Color.black },
            active = { background = pressedOrange, textColor = Color.black },
            margin = new RectOffset(0, 0, 6, 4), fixedHeight = 38f,
        };
        ButtonGhost = new GUIStyle(Button)
        {
            normal = { background = framedDim, textColor = Text },
            hover = { background = framedOrange, textColor = Orange },
            active = { background = framedOrange, textColor = Orange },
            fixedHeight = 32f,
        };
        Input = new GUIStyle
        {
            fontSize = 16, normal = { background = framedOrange, textColor = Text }, focused = { background = framedOrange, textColor = Text },
            border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(10, 10, 10, 7), clipping = TextClipping.Clip,
        };
        Toast = new GUIStyle(Panel) { fontSize = 16, alignment = TextAnchor.MiddleCenter, normal = { background = panel, textColor = Text }, wordWrap = true, richText = true, padding = new RectOffset(18, 18, 10, 10) };
        ChatLine = new GUIStyle { fontSize = 16, normal = { textColor = Text }, richText = true, wordWrap = true, padding = new RectOffset(0, 0, 1, 1) };
        NameTag = new GUIStyle { fontSize = 16, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
    }

    private static void ApplyFont(Font font)
    {
        foreach (GUIStyle s in new[] { Title, Small, Label, Section, Button, ButtonGhost, Input, Toast, ChatLine, NameTag })
        {
            s.font = font;
        }
    }

    private static Font SystemFont()
    {
        try
        {
            return Font.CreateDynamicFontFromOSFont(new[] { "Perfect DOS VGA 437", "Consolas", "Lucida Console", "Courier New" }, 16);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[coop] ui: no system font: " + e.Message);
            return null;
        }
    }

    private static void LookForGameFont()
    {
        bool first = _fontLooks == 0;
        _fontLooks++;
        try
        {
            if (first)
            {
                Resources.LoadAll<Font>("");
            }
            Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
            foreach (Font f in fonts)
            {
                // Dynamic: signs the font does not have (names in other scripts) come from a
                // fallback font instead of staying empty.
                if (f != null && f.dynamic && IsGameFont(f))
                {
                    _gameFont = true;
                    ApplyFont(f);
                    Plugin.Log.LogInfo($"[coop] ui: using the game's font {f.name}");
                    return;
                }
            }
            if (first || _fontLooks == MaxFontLooks)
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (Font f in fonts)
                {
                    names.Add($"{f.name} (dynamic: {f.dynamic})");
                }
                Plugin.Log.LogInfo($"[coop] ui: game font not loaded, using a system font. Loaded: {string.Join(", ", names)}");
            }
        }
        catch (Exception e)
        {
            _fontLooks = MaxFontLooks;
            Plugin.Log.LogWarning("[coop] ui: font lookup failed: " + e.Message);
        }
    }

    private static bool IsGameFont(Font f)
    {
        if (Matches(f.name))
        {
            return true;
        }
        if (f.fontNames != null)
        {
            foreach (string n in f.fontNames)
            {
                if (Matches(n))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool Matches(string name)
    {
        return name != null && (name.IndexOf("437", StringComparison.Ordinal) >= 0 || name.IndexOf("PerfectDOS", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Perfect DOS", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

    public static string Colored(string text, Color c) => $"<color=#{Hex(c)}>{text}</color>";

    // The game's coin sign, in its color.
    public static string Coins(object amount) => amount + " " + Colored("Φ", Orange);

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
