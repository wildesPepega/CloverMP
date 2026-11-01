using Panik;
using UnityEngine;

namespace CloverPitCoop.Net;

// While someone else plays at the slot machine, everyone else sees the machine in a small
// window (a second camera at the game's own "sitting at the machine" camera point) plus a status
// line, wherever they are in the room. The reels, wins and charm triggers run on every machine
// (lockstep), so this shows the real thing, not a stream.
internal static class Spectate
{
    private const int Width = 512;
    private const int Height = 288;

    private static Camera _cam;
    private static RenderTexture _rt;

    private static bool ShouldShow()
    {
        return LocalFreedom.Active && CameraController.instance != null && CameraController.instance.slotTransform != null
            && !CoopMenu.IsOpen;
    }

    // Called every frame (LateUpdate timing is not needed: the camera renders on its own).
    private static GameplayMaster _scene;

    public static void Tick()
    {
        if (GameplayMaster.instance != _scene)
        {
            _scene = GameplayMaster.instance;
            Reset();
        }
        bool show = ShouldShow();
        if (!show)
        {
            if (_cam != null && _cam.enabled)
            {
                _cam.enabled = false;
            }
            return;
        }
        if (_cam == null)
        {
            Create();
            if (_cam == null)
            {
                return;
            }
        }
        Transform seat = CameraController.instance.slotTransform;
        _cam.transform.SetPositionAndRotation(seat.position, seat.rotation);
        _cam.enabled = true;
    }

    private static void Create()
    {
        Camera main = CameraGame.firstInstance != null ? CameraGame.firstInstance.myCamera : Camera.main;
        if (main == null)
        {
            return;
        }
        _rt = new RenderTexture(Width, Height, 24) { name = "coop-spectate" };
        var go = new GameObject("coop-spectate-camera");
        Object.DontDestroyOnLoad(go);
        _cam = go.AddComponent<Camera>();
        _cam.CopyFrom(main);
        _cam.targetTexture = _rt;
        _cam.depth = main.depth - 1f;
        _cam.enabled = false;
        Plugin.Log.LogInfo("[coop] spectate: machine view camera created");
    }

    public static void OnGUI()
    {
        if (!ShouldShow() || _rt == null)
        {
            return;
        }
        UiTheme.Ensure();
        float w = Mathf.Min(UiTheme.Px(Width), Screen.width * 0.32f);
        float h = w * Height / Width;
        float pad = UiTheme.Px(8f);
        var r = new Rect(Screen.width - w - UiTheme.Px(18f), UiTheme.Px(18f), w, h);
        GUI.Box(new Rect(r.x - pad, r.y - pad, r.width + 2f * pad, r.height + pad + UiTheme.Px(32f)), GUIContent.none, UiTheme.Panel);
        GUI.DrawTexture(r, _rt, ScaleMode.ScaleToFit, false);
        string line = $"{Social.ColoredName(Coop.MachineOwner)} · {GameplayData.SpinsLeftGet()} spins · {UiTheme.Coins(GameplayData.CoinsGet())}";
        GUI.Label(new Rect(r.x + UiTheme.Px(2f), r.yMax + UiTheme.Px(4f), r.width, UiTheme.Px(24f)), line, UiTheme.Label);
    }

    // The scene (and with it the camera's settings) changes on every new run.
    public static void Reset()
    {
        if (_cam != null)
        {
            Object.Destroy(_cam.gameObject);
            _cam = null;
        }
        if (_rt != null)
        {
            _rt.Release();
            _rt = null;
        }
    }
}
