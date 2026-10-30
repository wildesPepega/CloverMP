using UnityEngine;

namespace CloverPitCoop.Net;

// Soft two-note blip for a chat message from a teammate. Generated here (no asset) and played
// on the mod's own 2D AudioSource, so it does not depend on which game sounds are loaded.
internal static class ChatSound
{
    private const int Rate = 44100;
    private static AudioSource _source;
    private static AudioClip _clip;

    public static void Play()
    {
        if (_source == null)
        {
            var go = new GameObject("coop-chat-sound");
            Object.DontDestroyOnLoad(go);
            _source = go.AddComponent<AudioSource>();
            _source.spatialBlend = 0f;
            _source.playOnAwake = false;
            _source.ignoreListenerPause = true;
            _source.volume = 0.35f;
        }
        _clip ??= Make();
        _source.PlayOneShot(_clip);
        Plugin.Log.LogInfo("[coop] chat: sound played");
    }

    // 880 Hz then 1320 Hz, 70 ms each, soft attack and fade so it does not click.
    private static AudioClip Make()
    {
        const float noteSeconds = 0.07f;
        int note = (int)(Rate * noteSeconds);
        var data = new float[note * 2];
        for (int i = 0; i < data.Length; i++)
        {
            float freq = i < note ? 880f : 1320f;
            int k = i % note;
            float env = Mathf.Min(1f, k / (Rate * 0.005f)) * Mathf.Clamp01(1f - (float)k / note);
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * i / Rate) * env * 0.6f;
        }
        AudioClip clip = AudioClip.Create("coop-chat-blip", data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
