using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace CloverPitCoop.Net;

// Looks for a newer release of this mod on GitHub when the game starts, downloads it and restarts
// the game on it. Off switch in the F8 menu and in the config file. Pre-releases count, all
// releases so far are pre-releases.
//
// What it refuses: anything but the DLL of this repository, an older or equal version, a file whose
// SHA-256 differs from what GitHub lists for it, an assembly that is not this mod in that version,
// and a version it already tried once (a broken release cannot put the game in a restart loop).
//
// The running game keeps its DLL loaded, so a small script replaces it after the game has exited
// and starts the game again. A player already standing in the room is not interrupted: the file is
// replaced when they close the game.
internal static class AutoUpdate
{
    private const string ApiUrl = "https://api.github.com/repos/wildesPepega/CloverMP/releases?per_page=10";
    private const string DownloadPrefix = "https://github.com/wildesPepega/CloverMP/releases/download/";
    private const string AssetName = "CloverPitCoop.dll";
    private const string AssemblyShortName = "CloverPitCoop";
    private const string Downloaded = "CloverPitCoop.dll.update";
    private const string ScriptName = "CloverPitCoop-update.cmd";
    private const float RestartNoticeSeconds = 3f;
    private const int RequestTimeoutSeconds = 30;

    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<string> LastTried;
    public static string Status = "";

    // Filled by JsonUtility.
#pragma warning disable CS0649
    [Serializable]
    private class Asset
    {
        public string name;
        public string browser_download_url;
        public string digest;
    }

    [Serializable]
    private class Release
    {
        public string tag_name;
        public bool draft;
        public Asset[] assets;
    }

    [Serializable]
    private class ReleaseList
    {
        public Release[] items;
    }

#pragma warning restore CS0649

    public static IEnumerator Run()
    {
        string folder = Path.GetDirectoryName(typeof(AutoUpdate).Assembly.Location);
        if (string.IsNullOrEmpty(folder) || !Enabled.Value)
        {
            yield break;
        }
        DeleteQuietly(Path.Combine(folder, Downloaded));

        string list = null;
        yield return Fetch(ApiUrl, r => list = r.downloadHandler.text);
        if (list == null)
        {
            yield break;
        }
        if (!NewestRelease(list, out Release release, out Asset asset, out Version version))
        {
            Status = "Up to date";
            Plugin.Log.LogInfo($"[coop] update: {Plugin.Version} is up to date");
            yield break;
        }
        if (LastTried.Value == release.tag_name)
        {
            Status = $"{release.tag_name} did not install";
            Plugin.Log.LogWarning($"[coop] update: {release.tag_name} was already tried, not again");
            yield break;
        }
        if (!asset.browser_download_url.StartsWith(DownloadPrefix, StringComparison.Ordinal)
            || asset.digest == null || !asset.digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            Plugin.Log.LogWarning($"[coop] update: refused {release.tag_name}, unexpected address or no checksum");
            yield break;
        }

        Plugin.Log.LogInfo($"[coop] update: downloading {release.tag_name}");
        Status = $"Downloading {release.tag_name} …";
        byte[] file = null;
        yield return Fetch(asset.browser_download_url, r => file = r.downloadHandler.data);
        if (file == null)
        {
            yield break;
        }

        string path = Path.Combine(folder, Downloaded);
        if (!Verify(file, asset.digest, version, path))
        {
            DeleteQuietly(path);
            Status = $"{release.tag_name} failed the checks";
            yield break;
        }

        // Before anything can go wrong: this version gets exactly one try.
        LastTried.Value = release.tag_name;
        bool restart = Coop.Role == Role.Off && !CoopMenu.InGame();
        if (!StartScript(folder, path, restart))
        {
            DeleteQuietly(path);
            Status = $"{release.tag_name} could not be installed";
            yield break;
        }
        if (!restart)
        {
            Status = $"{release.tag_name} installs when you close the game";
            Coop.Toast = $"CloverMP {release.tag_name} installs when you close the game";
            Coop.ToastUntil = Time.unscaledTime + 6f;
            Plugin.Log.LogInfo($"[coop] update: {release.tag_name} ready, player is busy, installs on exit");
            yield break;
        }
        Plugin.Log.LogInfo($"[coop] update: {release.tag_name} ready, restarting the game");
        Coop.Toast = $"CloverMP updated to {release.tag_name} – restarting the game";
        Coop.ToastUntil = Time.unscaledTime + RestartNoticeSeconds + 5f;
        yield return new WaitForSecondsRealtime(RestartNoticeSeconds);
        Application.Quit();
    }

    private static IEnumerator Fetch(string url, Action<UnityWebRequest> onSuccess)
    {
        using UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = RequestTimeoutSeconds;
        // GitHub answers no request without one.
        request.SetRequestHeader("User-Agent", "CloverMP/" + Plugin.Version);
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            // Offline or GitHub unreachable: just play.
            Plugin.Log.LogInfo($"[coop] update: no answer from GitHub ({request.error})");
            yield break;
        }
        onSuccess(request);
    }

    private static bool NewestRelease(string json, out Release newest, out Asset asset, out Version version)
    {
        newest = null;
        asset = null;
        version = new Version(Plugin.Version);
        ReleaseList list;
        try
        {
            list = JsonUtility.FromJson<ReleaseList>("{\"items\":" + json + "}");
        }
        catch (ArgumentException e)
        {
            Plugin.Log.LogWarning("[coop] update: could not read the release list: " + e.Message);
            return false;
        }
        if (list?.items == null)
        {
            return false;
        }
        foreach (Release r in list.items)
        {
            Asset dll = r.assets?.FirstOrDefault(a => a.name == AssetName);
            if (!r.draft && dll != null && r.tag_name != null && r.tag_name.StartsWith("v", StringComparison.Ordinal)
                && Version.TryParse(r.tag_name.Substring(1), out Version v) && v > version)
            {
                newest = r;
                asset = dll;
                version = v;
            }
        }
        return newest != null;
    }

    private static bool Verify(byte[] file, string digest, Version version, string path)
    {
        using SHA256 sha = SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        if ("sha256:" + hash != digest)
        {
            Plugin.Log.LogWarning($"[coop] update: checksum differs ({hash} instead of {digest})");
            return false;
        }
        try
        {
            File.WriteAllBytes(path, file);
            AssemblyName name = AssemblyName.GetAssemblyName(path);
            Version v = name.Version;
            if (name.Name != AssemblyShortName || v.Major != version.Major || v.Minor != version.Minor || v.Build != version.Build)
            {
                Plugin.Log.LogWarning($"[coop] update: the file is {name.Name} {v}, expected {AssemblyShortName} {version}");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[coop] update: could not check the file: " + e.Message);
            return false;
        }
    }

    private static bool StartScript(string folder, string downloaded, bool restart)
    {
        try
        {
            string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => "\"" + a.Replace("\"", "") + "\""));
            string script = BuildScript(Process.GetCurrentProcess().Id, downloaded, Path.Combine(folder, AssetName),
                restart, Paths.GameRootPath, Paths.ExecutablePath, args);
            string scriptPath = Path.Combine(folder, ScriptName);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{scriptPath}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            });
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[coop] update: could not start the update script: " + e.Message);
            return false;
        }
    }

    // Waits until the game has exited, puts the new DLL in place and starts the game again. If the
    // file cannot be replaced the game still comes back, on the old version.
    // The game's mod loader (Doorstop) leaves variables behind to mark a running game; started
    // with them, the new game would run without any mods.
    internal static string BuildScript(int gameProcess, string downloaded, string dll, bool restart, string gameFolder, string exe, string args)
    {
        string Esc(string s) => s.Replace("%", "%%");
        var s = new StringBuilder();
        s.Append("@echo off\r\n");
        s.Append("chcp 65001 >nul\r\n");
        s.Append("set tries=0\r\n");
        s.Append(":wait\r\n");
        // Full path: a "find" from other tools on the PATH (Git) never matches, the wait would end at once.
        s.Append($"tasklist /fi \"PID eq {gameProcess}\" 2>nul | \"%SystemRoot%\\System32\\find.exe\" \"{gameProcess}\" >nul\r\n");
        s.Append("if errorlevel 1 goto swap\r\n");
        s.Append("ping -n 2 127.0.0.1 >nul\r\n");
        s.Append("goto wait\r\n");
        s.Append(":swap\r\n");
        s.Append($"move /y \"{Esc(downloaded)}\" \"{Esc(dll)}\" >nul 2>&1\r\n");
        s.Append("if not errorlevel 1 goto swapped\r\n");
        s.Append("set /a tries+=1\r\n");
        s.Append("if %tries% geq 30 goto swapped\r\n");
        s.Append("ping -n 2 127.0.0.1 >nul\r\n");
        s.Append("goto swap\r\n");
        s.Append(":swapped\r\n");
        if (restart)
        {
            foreach (string name in new[] { "DOORSTOP_INITIALIZED", "DOORSTOP_DISABLE", "DOORSTOP_INVOKE_DLL_PATH", "DOORSTOP_MANAGED_FOLDER_DIR", "DOORSTOP_PROCESS_PATH", "DOORSTOP_DLL_SEARCH_DIRS", "DOORSTOP_MONO_LIB_PATH" })
            {
                s.Append($"set {name}=\r\n");
            }
            s.Append($"start \"\" /d \"{Esc(gameFolder)}\" \"{Esc(exe)}\" {Esc(args)}\r\n");
        }
        s.Append("(goto) 2>nul & del \"%~f0\"\r\n");
        return s.ToString();
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Overwritten by the next download anyway.
        }
    }
}
