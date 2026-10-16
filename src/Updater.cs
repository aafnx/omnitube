using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniTube;

/// <summary>
/// Self-update from the "latest" GitHub release, designed to never interrupt watching:
///
/// 1. While the app runs, it checks the release (by git commit, embedded at build time),
///    downloads and unpacks a newer build into %APPDATA%\OmniTube\update\staged.
/// 2. On a normal exit (YouTube's "exit", Alt+F4, window close) — after Chromium has shut down —
///    the staged files are swapped into the app folder. Files in use are renamed aside
///    (*.omnitube-old) rather than overwritten, which Windows allows for loaded exe/dll files.
/// 3. On the next start the renamed files are moved to %APPDATA%\OmniTube\previous — a full
///    copy of the prior version, for rollback.
///
/// If the process is killed instead of exiting normally, the staged update simply waits for
/// the next normal exit.
/// </summary>
internal static class Updater
{
    private const string OldSuffix = ".omnitube-old";
    private const string AssetName = "OmniTube-win-x64.zip";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private static string _dataDir;
    private static string _repo;
    private static string _appDir;
    private static string _updateDir;
    private static string _stagedDir;
    private static string _readyFile;
    private static string _previousDir;
    private static volatile bool _busy;

    /// <summary>Git commit this build was made from (empty for local builds).</summary>
    public static string CurrentCommit { get; private set; } = "";

    private sealed class ReadyInfo
    {
        public string Commit { get; set; }
        public string PublishedAt { get; set; }
    }

    public static void Init(string dataDir, string repo)
    {
        _dataDir = dataDir;
        _repo = repo;
        _appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        _updateDir = Path.Combine(dataDir, "update");
        _stagedDir = Path.Combine(_updateDir, "staged");
        _readyFile = Path.Combine(_updateDir, "ready.json");
        _previousDir = Path.Combine(dataDir, "previous");

        string info = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = info.IndexOf('+');
        CurrentCommit = plus >= 0 ? info[(plus + 1)..].Trim() : "";
        Log.Info($"Version {info} in {_appDir}");
    }

    // ------------------------------------------------------------------ startup: previous version

    /// <summary>Moves files renamed aside by the last update into %APPDATA%\OmniTube\previous.</summary>
    public static void CollectOldFiles()
    {
        try
        {
            var old = Directory.EnumerateFiles(_appDir, "*" + OldSuffix, SearchOption.AllDirectories).ToList();
            if (old.Count == 0) return;

            // the previous update's file list says which files belonged to the old version
            if (Directory.Exists(_previousDir)) Directory.Delete(_previousDir, recursive: true);
            Directory.CreateDirectory(_previousDir);

            int moved = 0;
            foreach (string file in old)
            {
                string rel = Path.GetRelativePath(_appDir, file[..^OldSuffix.Length]);
                string dest = Path.Combine(_previousDir, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    MoveFileAnyVolume(file, dest);
                    moved++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not move {rel} of the previous version: {ex.Message}");
                }
            }
            Log.Info($"Previous version kept in {_previousDir} ({moved} files)");
        }
        catch (Exception ex)
        {
            Log.Warn("Cleaning up after update failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ background check + download

    public static void StartBackgroundChecks(Action<string> onStaged)
    {
        if (string.IsNullOrEmpty(CurrentCommit))
        {
            Log.Info("Auto-update off: this build has no commit id (local build)");
            return;
        }

        Task.Run(async () =>
        {
            await Task.Delay(FirstCheckDelay);
            while (true)
            {
                try
                {
                    string staged = await CheckAndStageAsync();
                    if (staged != null) onStaged?.Invoke(staged);
                }
                catch (Exception ex)
                {
                    Log.Warn("Update check failed: " + ex.Message);
                }
                await Task.Delay(CheckInterval);
            }
        });
    }

    /// <returns>Commit of a newly staged update, or null.</returns>
    private static async Task<string> CheckAndStageAsync()
    {
        if (_busy) return null;
        _busy = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniTube-Updater/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync($"https://api.github.com/repos/{_repo}/releases/tags/latest");
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"Update check: release not available (HTTP {(int)response.StatusCode})");
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            string commit = root.GetProperty("target_commitish").GetString() ?? "";
            string published = root.TryGetProperty("published_at", out var p) ? p.GetString() : "";

            if (string.IsNullOrEmpty(commit) || commit.StartsWith(CurrentCommit, StringComparison.OrdinalIgnoreCase)
                || CurrentCommit.StartsWith(commit, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Update check: up to date ({Short(CurrentCommit)})");
                return null;
            }

            var ready = ReadReady();
            if (ready != null && string.Equals(ready.Commit, commit, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_stagedDir))
            {
                Log.Info($"Update check: {Short(commit)} already downloaded, waiting for exit");
                return null;
            }

            string url = null;
            long size = 0;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == AssetName)
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    size = asset.GetProperty("size").GetInt64();
                }
            }
            if (url == null)
            {
                Log.Warn("Update check: release has no " + AssetName);
                return null;
            }

            Log.Info($"Update {Short(CurrentCommit)} -> {Short(commit)} ({published}), downloading {size / (1024 * 1024)} MB");

            Directory.CreateDirectory(_updateDir);
            string zip = Path.Combine(_updateDir, "download.zip");
            await DownloadAsync(http, url, zip, size);

            // unpack into a temp folder, validate, then make it the staged update
            string unpack = Path.Combine(_updateDir, "unpack");
            if (Directory.Exists(unpack)) Directory.Delete(unpack, recursive: true);
            ZipFile.ExtractToDirectory(zip, unpack);
            File.Delete(zip);

            foreach (string required in new[] { "OmniTube.exe", "OmniTube.dll", "libcef.dll", "CefSharp.Core.Runtime.dll" })
            {
                if (!File.Exists(Path.Combine(unpack, required)))
                    throw new InvalidDataException($"downloaded build is missing {required}");
            }

            File.Delete(_readyFile);
            if (Directory.Exists(_stagedDir)) Directory.Delete(_stagedDir, recursive: true);
            Directory.Move(unpack, _stagedDir);
            File.WriteAllText(_readyFile, JsonSerializer.Serialize(new ReadyInfo { Commit = commit, PublishedAt = published }));

            Log.Info($"Update {Short(commit)} staged, will be installed on exit");
            return commit;
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, long expected)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long total = 0;
        int lastPercent = 0;
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                total += read;
                if (expected > 0)
                {
                    int percent = (int)(total * 100 / expected);
                    if (percent >= lastPercent + 25)
                    {
                        lastPercent = percent;
                        Log.Info($"Update download {percent}%");
                    }
                }
            }
        }

        if (expected > 0 && total != expected)
            throw new InvalidDataException($"download size {total} != expected {expected}");
    }

    // ------------------------------------------------------------------ exit: install

    /// <summary>Call after Chromium has shut down. Swaps the staged build into the app folder.</summary>
    public static void ApplyStagedUpdate()
    {
        try
        {
            var ready = ReadReady();
            if (ready == null || !Directory.Exists(_stagedDir)) return;

            if (string.Equals(ready.Commit, CurrentCommit, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(_stagedDir, recursive: true);
                File.Delete(_readyFile);
                return;
            }

            WaitForChildProcesses(TimeSpan.FromSeconds(10));

            Log.Info($"Installing update {Short(ready.Commit)}");
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var renamed = new List<(string Target, string Aside)>();
            var placed = new List<string>();

            try
            {
                foreach (string source in Directory.EnumerateFiles(_stagedDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(_stagedDir, source);
                    string target = Path.Combine(_appDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    if (File.Exists(target))
                    {
                        string aside = target + OldSuffix;
                        if (File.Exists(aside)) File.Move(aside, aside + "." + stamp); // stale leftover
                        File.Move(target, aside);
                        renamed.Add((target, aside));
                    }

                    File.Move(source, target);
                    placed.Add(target);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Installing update failed, rolling back", ex);
                foreach (string file in placed)
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var (target, aside) in Enumerable.Reverse(renamed))
                {
                    try { File.Move(aside, target, overwrite: true); } catch { }
                }
                return;
            }

            Directory.Delete(_stagedDir, recursive: true);
            File.Delete(_readyFile);
            Log.Info($"Update {Short(ready.Commit)} installed ({placed.Count} files); active on next start");
        }
        catch (Exception ex)
        {
            Log.Error("Applying update failed", ex);
        }
    }

    private static void WaitForChildProcesses(TimeSpan timeout)
    {
        string self = Environment.ProcessPath ?? "";
        int selfId = Environment.ProcessId;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            bool any = false;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(self)))
            {
                try
                {
                    if (p.Id != selfId && string.Equals(p.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase))
                        any = true;
                }
                catch
                {
                    // access denied for other users' processes: not ours
                }
                finally
                {
                    p.Dispose();
                }
            }

            if (!any) return;
            Thread.Sleep(200);
        }

        Log.Warn("Chromium helper processes still running, installing anyway");
    }

    // ------------------------------------------------------------------ helpers

    private static ReadyInfo ReadReady()
    {
        try
        {
            return File.Exists(_readyFile) ? JsonSerializer.Deserialize<ReadyInfo>(File.ReadAllText(_readyFile)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void MoveFileAnyVolume(string source, string dest)
    {
        try
        {
            File.Move(source, dest, overwrite: true);
        }
        catch (IOException)
        {
            // different volume: copy then delete
            File.Copy(source, dest, overwrite: true);
            File.Delete(source);
        }
    }

    private static string Short(string commit) =>
        string.IsNullOrEmpty(commit) ? "local" : commit[..Math.Min(7, commit.Length)];
}
