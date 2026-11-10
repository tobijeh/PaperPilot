using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using PaperPilot.Diagnostics;

namespace PaperPilot.Core;

public sealed record UpdateInfo(Version Version, string Tag, string ExeUrl, long ExeSize, string? Sha256Url, string Notes);

/// <summary>
/// Self-update from GitHub Releases. Network is only touched when a check or install is
/// explicitly started (app startup / "Check for updates"), never while Paper Pilot is flying.
/// </summary>
public static class Updater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/tobijeh/PaperPilot/releases/latest";
    private const string ExeAssetName = "PaperPilot.exe";
    private const string ShaAssetName = "PaperPilot.exe.sha256";
    public const string WaitForPidArg = "--wait-for-pid";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Local builds are 0.x.0; only CI builds (0.x.N with N &gt; 0) update themselves.</summary>
    public static bool IsReleaseBuild => CurrentVersion.Build > 0;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PaperPilot-Updater");
        return client;
    }

    /// <summary>Returns the newer release, or null if this is already the newest.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Info("Update: no releases published yet.");
            return null;
        }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
        {
            Log.Warn($"Update: can't parse release tag '{tag}'.");
            return null;
        }
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));

        string? exeUrl = null, shaUrl = null;
        long exeSize = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == ExeAssetName) { exeUrl = url; exeSize = asset.GetProperty("size").GetInt64(); }
            else if (name == ShaAssetName) shaUrl = url;
        }
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        Log.Info($"Update: current {CurrentVersion}, latest {latest} ({tag}).");
        if (latest <= CurrentVersion || exeUrl is null) return null;
        return new UpdateInfo(latest, tag, exeUrl, exeSize, shaUrl, notes);
    }

    /// <summary>
    /// Downloads the new exe, swaps it in place of the running one and starts it. The caller
    /// should shut the app down right after this returns.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var currentExe = Environment.ProcessPath;
        if (currentExe is null || !Path.GetFileName(currentExe).Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This copy is running from a development build and can't update itself. Download the latest PaperPilot.exe instead.");

        var dir = Path.GetDirectoryName(currentExe)!;
        var newExe = Path.Combine(dir, "PaperPilot.exe.new");
        var oldExe = Path.Combine(dir, "PaperPilot.exe.old");

        // ---- Download ------------------------------------------------------------------------
        Log.Info($"Update: downloading {info.Tag} ({info.ExeSize / 1024 / 1024} MB).");
        try
        {
            using (var resp = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? info.ExeSize;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(newExe);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Paper Pilot can't write to {dir}. Move PaperPilot.exe to a folder you own (e.g. Documents) and try again.");
        }

        // ---- Verify ----------------------------------------------------------------------------
        if (info.ExeSize > 0 && new FileInfo(newExe).Length != info.ExeSize)
        {
            File.Delete(newExe);
            throw new InvalidOperationException("The download was incomplete. Please try again.");
        }
        if (info.Sha256Url is not null)
        {
            var expected = (await Http.GetStringAsync(info.Sha256Url, ct)).Trim().Split(' ', '\t')[0];
            string actual;
            await using (var fs = File.OpenRead(newExe))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(newExe);
                throw new InvalidOperationException("The download didn't pass its integrity check. Please try again.");
            }
        }

        // ---- Swap and restart --------------------------------------------------------------
        // Windows lets us rename a running exe, so: current -> .old, new -> current.
        if (File.Exists(oldExe)) File.Delete(oldExe);
        File.Move(currentExe, oldExe);
        try
        {
            File.Move(newExe, currentExe);
        }
        catch
        {
            File.Move(oldExe, currentExe); // roll back
            throw;
        }

        Log.Info($"Update: installed {info.Tag}, restarting.");
        Process.Start(new ProcessStartInfo(currentExe, $"{WaitForPidArg} {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>
    /// Called at startup: waits for the previous copy to exit (so hotkeys/capture are free) and
    /// removes leftovers from the last update.
    /// </summary>
    public static void OnStartup(string[] args)
    {
        int i = Array.IndexOf(args, WaitForPidArg);
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(15000);
            }
            catch
            {
                // already gone
            }
            Log.Info($"Update: now running {CurrentVersion}.");
        }

        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var dir = Path.GetDirectoryName(exe)!;
        foreach (var leftover in new[] { "PaperPilot.exe.old", "PaperPilot.exe.new" })
        {
            try { File.Delete(Path.Combine(dir, leftover)); }
            catch { /* still locked; next start will get it */ }
        }
    }
}
