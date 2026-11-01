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
    private const string UpdateLogName = "CloverMP-update.log";
    private const long UpdateLogMaxBytes = 64 * 1024;

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
        StartLogFile();
        Note($"this is {Plugin.Version}, asking GitHub");

        string list = null;
        // GitHub's servers answer the same address from a 60 s cache, which hides a release made
        // just now. A new address every time goes around it.
        yield return Fetch($"{ApiUrl}&nocache={DateTime.UtcNow.Ticks}", r => list = r.downloadHandler.text);
        if (list == null)
        {
            Fail("Could not reach GitHub", false);
            yield break;
        }
        if (!NewestRelease(list, out Release release, out Asset asset, out Version version, out string seen))
        {
            Status = "Up to date";
            Note($"{Plugin.Version} is up to date ({seen})");
            yield break;
        }
        Note($"found {release.tag_name} ({seen})");
        if (LastTried.Value == release.tag_name)
        {
            Fail($"{release.tag_name} did not install last time, install it by hand", false);
            yield break;
        }
        if (!asset.browser_download_url.StartsWith(DownloadPrefix, StringComparison.Ordinal)
            || asset.digest == null || !asset.digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            Note($"unexpected address or no checksum: {asset.browser_download_url}, {asset.digest}");
            Fail($"{release.tag_name} was refused", true);
            yield break;
        }

        Note($"downloading {release.tag_name}");
        Status = $"Downloading {release.tag_name} …";
        byte[] file = null;
        yield return Fetch(asset.browser_download_url, r => file = r.downloadHandler.data);
        if (file == null)
        {
            Fail($"{release.tag_name} could not be downloaded", true);
            yield break;
        }
        Note($"downloaded {file.Length} bytes");

        string path = Path.Combine(folder, Downloaded);
        if (!Verify(file, asset.digest, version, path))
        {
            DeleteQuietly(path);
            Fail($"{release.tag_name} failed the checks", true);
            yield break;
        }
        Note("checks passed");

        // Before anything can go wrong: this version gets exactly one try.
        LastTried.Value = release.tag_name;
        bool restart = Coop.Role == Role.Off && !CoopMenu.InGame();
        if (!StartScript(folder, path, restart))
        {
            DeleteQuietly(path);
            Fail($"{release.tag_name} could not be installed", true);
            yield break;
        }
        if (!restart)
        {
            Status = $"{release.tag_name} installs when you close the game";
            Coop.Toast = $"CloverMP {release.tag_name} installs when you close the game";
            Coop.ToastUntil = Time.unscaledTime + 6f;
            Note($"{release.tag_name} ready, player is busy, installs on exit");
            yield break;
        }
        Note($"{release.tag_name} ready, restarting the game");
        Coop.Toast = $"CloverMP updated to {release.tag_name} – restarting the game";
        Coop.ToastUntil = Time.unscaledTime + RestartNoticeSeconds + 5f;
        yield return new WaitForSecondsRealtime(RestartNoticeSeconds);
        Application.Quit();
    }

    // The game's own log starts over with every game start, so what happened before an update's
    // restart would be gone. This file stays.
    private static string LogFile => Path.Combine(Paths.BepInExRootPath, UpdateLogName);

    private static void StartLogFile()
    {
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > UpdateLogMaxBytes)
            {
                File.Delete(LogFile);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[coop] update: log file: " + e.Message);
        }
    }

    private static void Note(string text)
    {
        Plugin.Log.LogInfo("[coop] update: " + text);
        try
        {
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\r\n");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[coop] update: log file: " + e.Message);
        }
    }

    // Shown in the F8 menu; on the screen too when a found update did not make it.
    private static void Fail(string text, bool toast)
    {
        Status = text;
        Note(text);
        if (toast)
        {
            Coop.Toast = "CloverMP update: " + text;
            Coop.ToastUntil = Time.unscaledTime + 8f;
        }
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
            Note($"no answer from {url}: {request.error} (HTTP {request.responseCode})");
            yield break;
        }
        onSuccess(request);
    }

    private static bool NewestRelease(string json, out Release newest, out Asset asset, out Version version, out string seen)
    {
        newest = null;
        asset = null;
        version = new Version(Plugin.Version);
        seen = "could not read the release list";
        ReleaseList list;
        try
        {
            list = JsonUtility.FromJson<ReleaseList>("{\"items\":" + json + "}");
        }
        catch (ArgumentException e)
        {
            Note("could not read the release list: " + e.Message);
            return false;
        }
        if (list?.items == null)
        {
            return false;
        }
        seen = $"{list.items.Length} releases, first {(list.items.Length > 0 ? list.items[0].tag_name : "none")}";
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
            Note($"checksum differs ({hash} instead of {digest})");
            return false;
        }
        try
        {
            File.WriteAllBytes(path, file);
            AssemblyName name = AssemblyName.GetAssemblyName(path);
            Version v = name.Version;
            if (name.Name != AssemblyShortName || v.Major != version.Major || v.Minor != version.Minor || v.Build != version.Build)
            {
                Note($"the file is {name.Name} {v}, expected {AssemblyShortName} {version}");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Note("could not check the file: " + e);
            return false;
        }
    }

    private static bool StartScript(string folder, string downloaded, bool restart)
    {
        try
        {
            string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => "\"" + a.Replace("\"", "") + "\""));
            string script = BuildScript(Process.GetCurrentProcess().Id, downloaded, Path.Combine(folder, AssetName),
                restart, Paths.GameRootPath, Paths.ExecutablePath, args, LogFile);
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
            Note("could not start the update script: " + e);
            return false;
        }
    }

    // Waits until the game has exited, puts the new DLL in place and starts the game again. If the
    // file cannot be replaced the game still comes back, on the old version.
    // The game's mod loader (Doorstop) leaves variables behind to mark a running game; started
    // with them, the new game would run without any mods.
    internal static string BuildScript(int gameProcess, string downloaded, string dll, bool restart, string gameFolder, string exe, string args, string logFile)
    {
        string Esc(string s) => s.Replace("%", "%%");
        string Log(string text) => $"(echo %date% %time% script: {text})>>\"{Esc(logFile)}\"\r\n";
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
        s.Append("if %tries% geq 30 goto failed\r\n");
        s.Append("ping -n 2 127.0.0.1 >nul\r\n");
        s.Append("goto swap\r\n");
        s.Append(":failed\r\n");
        s.Append(Log("could not replace the DLL"));
        s.Append("goto finish\r\n");
        s.Append(":swapped\r\n");
        s.Append(Log("DLL replaced"));
        s.Append(":finish\r\n");
        if (restart)
        {
            foreach (string name in new[] { "DOORSTOP_INITIALIZED", "DOORSTOP_DISABLE", "DOORSTOP_INVOKE_DLL_PATH", "DOORSTOP_MANAGED_FOLDER_DIR", "DOORSTOP_PROCESS_PATH", "DOORSTOP_DLL_SEARCH_DIRS", "DOORSTOP_MONO_LIB_PATH" })
            {
                s.Append($"set {name}=\r\n");
            }
            s.Append(Log("starting the game"));
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
