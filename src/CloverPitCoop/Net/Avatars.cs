using System.Collections.Generic;
using UnityEngine;

namespace CloverPitCoop.Net;

// Very simple stand-ins for the other players: a colored body, a head and a "visor" showing
// where they look, placed at their view camera (5 updates per second, smoothed). No colliders,
// so they never block walking, clicking or the game's raycasts. Built from Unity primitives,
// no game assets.
internal static class Avatars
{
    private static readonly Color[] Palette =
    {
        new Color(0.95f, 0.75f, 0.2f), new Color(0.3f, 0.75f, 1f), new Color(0.95f, 0.35f, 0.5f), new Color(0.45f, 0.9f, 0.45f),
    };

    private sealed class Avatar
    {
        public GameObject Root;
        public Vector3 TargetHead;
        public Quaternion TargetYaw;
        public float TargetPitch;
        public Transform Head;
        public float LastSeen;
    }

    private static readonly Dictionary<int, Avatar> All = new Dictionary<int, Avatar>();
    private static Material _shared;

    public static Color ColorOf(int player) => Palette[Mathf.Abs(player) % Palette.Length];

    public static Vector3 HeadOf(int player, Vector3 fallback)
    {
        return All.TryGetValue(player, out Avatar a) && a.Root != null ? a.Head.position : fallback;
    }

    public static void Set(int player, Vector3 head, float yaw, float pitch)
    {
        if (!All.TryGetValue(player, out Avatar a) || a.Root == null)
        {
            a = Build(player);
            All[player] = a;
            a.Root.transform.position = head;
        }
        a.TargetHead = head;
        a.TargetYaw = Quaternion.Euler(0f, yaw, 0f);
        a.TargetPitch = pitch;
        a.LastSeen = Time.unscaledTime;
    }

    public static void Tick()
    {
        // Hidden on this machine until it stands in the room itself (not in intro, title, menu
        // or while loading the host's run); a remote player shows only while it sends updates,
        // which it does only from the room.
        bool here = CoopMenu.InGame() && !JoinSync.Loading;
        foreach (Avatar a in All.Values)
        {
            if (a.Root == null)
            {
                continue;
            }
            bool fresh = here && Time.unscaledTime - a.LastSeen < 1.5f && Coop.Role != Role.Off;
            if (a.Root.activeSelf != fresh)
            {
                a.Root.SetActive(fresh);
            }
            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            Transform t = a.Root.transform;
            t.position = Vector3.Lerp(t.position, a.TargetHead, k);
            t.rotation = Quaternion.Slerp(t.rotation, a.TargetYaw, k);
            a.Head.localRotation = Quaternion.Slerp(a.Head.localRotation, Quaternion.Euler(a.TargetPitch, 0f, 0f), k);
        }
    }

    public static void Clear()
    {
        foreach (Avatar a in All.Values)
        {
            if (a.Root != null)
            {
                Object.Destroy(a.Root);
            }
        }
        All.Clear();
    }

    private static Avatar Build(int player)
    {
        Color c = ColorOf(player);
        var root = new GameObject($"coop-player-{player}");
        // The root sits at the head (= the other player's camera); the body hangs below it.
        GameObject body = Part(PrimitiveType.Capsule, root.transform, new Vector3(0f, -0.85f, 0f), new Vector3(0.45f, 0.6f, 0.45f), c * 0.8f);
        var head = new GameObject("head");
        head.transform.SetParent(root.transform, false);
        Part(PrimitiveType.Sphere, head.transform, Vector3.zero, Vector3.one * 0.3f, c);
        Part(PrimitiveType.Cube, head.transform, new Vector3(0f, 0.02f, 0.14f), new Vector3(0.22f, 0.07f, 0.06f), Color.black);
        body.name = "body";
        return new Avatar { Root = root, Head = head.transform };
    }

    private static GameObject Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        Renderer r = go.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.material = Mat();
        r.material.color = color;
        return go;
    }

    // Prefer an unlit shader (the room is dark); fall back to whatever the primitive came with.
    private static Material Mat()
    {
        if (_shared == null)
        {
            Shader s = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            _shared = s != null ? new Material(s) : null;
        }
        return _shared != null ? new Material(_shared) : new Material(Shader.Find("Hidden/InternalErrorShader"));
    }
}
