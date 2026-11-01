using System;
using System.IO;
using UnityEngine;

namespace CloverPitCoop.Net;

// Sound for a chat message from a teammate: a soft double pop like a messenger app. Generated
// here (no asset) and played on the mod's own 2D AudioSource, so it does not depend on which game
// sounds are loaded. A file "chat.wav" (PCM, 16 bit) next to the mod's DLL replaces it.
internal static class ChatSound
{
    private const int Rate = 44100;
    private const string CustomFile = "chat.wav";
    private static AudioSource _source;
    private static AudioClip _clip;

    public static void Play()
    {
        if (_source == null)
        {
            var go = new GameObject("coop-chat-sound");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _source = go.AddComponent<AudioSource>();
            _source.spatialBlend = 0f;
            _source.playOnAwake = false;
            _source.ignoreListenerPause = true;
            _source.volume = 0.5f;
        }
        _clip ??= LoadCustom() ?? Make();
        _source.PlayOneShot(_clip);
        Plugin.Log.LogInfo("[coop] chat: sound played");
    }

    // Two quick pops, the second one higher: each slides up fast and dies away within 100 ms.
    private static AudioClip Make()
    {
        var data = new float[(int)(Rate * 0.2f)];
        AddPop(data, 0f, 420f, 780f, 0.75f);
        AddPop(data, 0.075f, 560f, 1040f, 0.55f);
        AudioClip clip = AudioClip.Create("coop-chat-pop", data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static void AddPop(float[] data, float start, float fromHz, float toHz, float amp)
    {
        float phase = 0f;
        for (int i = (int)(start * Rate); i < data.Length; i++)
        {
            float t = i / (float)Rate - start;
            float freq = toHz - (toHz - fromHz) * Mathf.Exp(-t / 0.018f);
            phase += 2f * Mathf.PI * freq / Rate;
            float env = Mathf.Min(1f, t / 0.002f) * Mathf.Exp(-t / 0.028f);
            data[i] += (Mathf.Sin(phase) + 0.25f * Mathf.Sin(2f * phase)) * env * amp * 0.8f;
        }
    }

    private static AudioClip LoadCustom()
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(ChatSound).Assembly.Location) ?? "", CustomFile);
            if (!File.Exists(path))
            {
                return null;
            }
            AudioClip clip = ReadWav(File.ReadAllBytes(path));
            Plugin.Log.LogInfo(clip != null ? $"[coop] chat: using {CustomFile}" : $"[coop] chat: {CustomFile} is not a 16 bit PCM file, using the built-in sound");
            return clip;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[coop] chat: could not read {CustomFile}: {e.Message}");
            return null;
        }
    }

    private static AudioClip ReadWav(byte[] b)
    {
        if (b.Length < 12 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
        {
            return null;
        }
        int channels = 0;
        int rate = 0;
        for (int pos = 12; pos + 8 <= b.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (id == "fmt " && body + 16 <= b.Length)
            {
                if (BitConverter.ToInt16(b, body) != 1 || BitConverter.ToInt16(b, body + 14) != 16)
                {
                    return null;
                }
                channels = BitConverter.ToInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
            }
            else if (id == "data" && channels > 0)
            {
                int bytes = Mathf.Min(size, b.Length - body);
                var data = new float[bytes / 2];
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = BitConverter.ToInt16(b, body + i * 2) / 32768f;
                }
                AudioClip clip = AudioClip.Create("coop-chat-custom", data.Length / channels, channels, rate, false);
                clip.SetData(data, 0);
                return clip;
            }
            // Chunks are padded to an even size.
            pos = body + size + (size & 1);
        }
        return null;
    }
}
