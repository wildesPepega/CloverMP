using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CloverPitCoop;

// Hash of the run state, built the same way the game builds its save:
// GameplayData.Save_Prepare() mirrors live objects (charms, drawers, store) into
// serializable fields, then JsonUtility turns the whole GameplayData into JSON.
internal static class StateHash
{
    // Wall-clock fields that legitimately differ between machines; blanked before hashing.
    private static readonly Regex WallClock = new Regex("(\"stats_PlayTime_Seconds\":\\s*)-?\\d+", RegexOptions.Compiled);

    public static string Json()
    {
        GameplayData data = GameplayData.Instance;
        if (data == null)
        {
            return null;
        }
        data.Save_Prepare();
        return WallClock.Replace(JsonUtility.ToJson(data, true), "${1}0");
    }

    public static string Of(string json)
    {
        if (json == null)
        {
            return "none";
        }
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }
}

// Writes one JSON file per step so two runs can be diffed field by field.
internal sealed class StateTrace
{
    private readonly string _dir;
    private int _step;

    public StateTrace(string label)
    {
        _dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-trace", label);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
        Directory.CreateDirectory(_dir);
    }

    public string Record(string what)
    {
        string json = StateHash.Json();
        string hash = StateHash.Of(json);
        string name = $"{_step:00}-{what}";
        File.WriteAllText(Path.Combine(_dir, name + ".json"), json ?? "");
        File.AppendAllText(Path.Combine(_dir, "trace.txt"), $"{name} {hash}\n");
        Plugin.Log.LogInfo($"[coop] trace {name} {hash}");
        Net.Coop.HostRecorded(hash);
        _step++;
        return hash;
    }
}
