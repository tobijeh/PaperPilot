using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using PaperPilot.Diagnostics;

namespace PaperPilot.Core;

/// <summary>One downloadable file of a release, with its checksum file if published.</summary>
public sealed record ReleaseAsset(string Url, long Size, string? Sha256Url);

/// <param name="Exe">The single PaperPilot.exe (for copies run without installing).</param>
/// <param name="Setup">PaperPilot-Setup.exe (for installed copies).</param>
public sealed record UpdateInfo(Version Version, string Tag, ReleaseAsset? Exe, ReleaseAsset? Setup, string Notes);

/// <summary>
/// Self-update from GitHub Releases. Network is only touched when a check or install is
/// explicitly started (app startup / "Check for updates"), never while Paper Pilot is flying.
/// Installed copies (the normal case) download the next PaperPilot-Setup.exe and run it silently;
/// a copy run without installing swaps its own exe.
/// </summary>
public static class Updater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/tobijeh/PaperPilot/releases/latest";
    private const string ExeAssetName = "PaperPilot.exe";
    private const string SetupAssetName = "PaperPilot-Setup.exe";
    private const string SetupArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
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

        var urls = new Dictionary<string, (string url, long size)>();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            urls[name] = (asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
        }
        ReleaseAsset? Find(string name) => urls.TryGetValue(name, out var a)
            ? new ReleaseAsset(a.url, a.size, urls.TryGetValue(name + ".sha256", out var h) ? h.url : null)
            : null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        var info = new UpdateInfo(latest, tag, Find(ExeAssetName), Find(SetupAssetName), notes);
        Log.Info($"Update: current {CurrentVersion}, latest {latest} ({tag}).");
        if (latest <= CurrentVersion || AssetFor(info) is null) return null;
        return info;
    }

    /// <summary>Installed with PaperPilot-Setup.exe (the uninstaller sits next to the exe).</summary>
    public static bool IsInstalled =>
        Environment.ProcessPath is { } p && File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "unins000.exe"));

    /// <summary>True when this copy can update itself (a published build, installed or a plain PaperPilot.exe).</summary>
    public static bool CanSelfUpdate =>
        IsReleaseBuild && Environment.ProcessPath is { } p
        && (IsInstalled || Path.GetFileName(p).Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase));

    private static ReleaseAsset? AssetFor(UpdateInfo info) => IsInstalled ? info.Setup : info.Exe;

    /// <summary>Where the downloaded update waits until it's installed.</summary>
    public static string StagedPath => IsInstalled
        ? Path.Combine(Log.AppDataFolder, "updates", SetupAssetName)
        : Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "PaperPilot.exe.new");

    /// <summary>Downloads and verifies the update (the new setup, or the new exe), ready to install.</summary>
    public static async Task DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!CanSelfUpdate)
            throw new InvalidOperationException("This copy can't update itself. Install Paper Pilot with PaperPilot-Setup.exe instead.");
        var asset = AssetFor(info) ?? throw new InvalidOperationException("This release has nothing to install for this copy.");

        var path = StagedPath;
        var dir = Path.GetDirectoryName(path)!;
        Log.Info($"Update: downloading {info.Tag} ({asset.Size / 1024 / 1024} MB, {(IsInstalled ? "setup" : "exe")}).");
        try
        {
            Directory.CreateDirectory(dir);
            using var resp = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
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
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Paper Pilot can't write to {dir}. Install it with PaperPilot-Setup.exe and try again.");
        }

        if (asset.Size > 0 && new FileInfo(path).Length != asset.Size)
        {
            File.Delete(path);
            throw new InvalidOperationException("The download was incomplete. Paper Pilot will try again later.");
        }
        if (asset.Sha256Url is not null)
        {
            var expected = (await Http.GetStringAsync(asset.Sha256Url, ct)).Trim().Split(' ', '\t')[0];
            string actual;
            await using (var fs = File.OpenRead(path))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidOperationException("The download didn't pass its integrity check. Paper Pilot will try again later.");
            }
        }
        Log.Info($"Update: {info.Tag} downloaded and verified; installs on restart or quit.");
    }

    /// <summary>
    /// Installs the downloaded update. Installed copies run the new setup silently (it waits for
    /// Paper Pilot to close, installs, and with <paramref name="restart"/> starts it again).
    /// Plain-exe copies swap the exe (Windows allows renaming a running exe). The caller then quits.
    /// </summary>
    public static bool ApplyStaged(bool restart)
    {
        if (!CanSelfUpdate) return false;
        var staged = StagedPath;
        if (!File.Exists(staged)) return false;

        if (IsInstalled)
        {
            try
            {
                var args = SetupArgs + (restart ? "" : " /nolaunch=1");
                Process.Start(new ProcessStartInfo(staged, args) { UseShellExecute = true });
                Log.Info($"Update: running setup ({args}).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Update: could not start the setup", ex);
                return false;
            }
        }

        var currentExe = Environment.ProcessPath!;
        var oldExe = Path.Combine(Path.GetDirectoryName(currentExe)!, "PaperPilot.exe.old");
        try
        {
            if (File.Exists(oldExe)) File.Delete(oldExe);
            File.Move(currentExe, oldExe);
            try
            {
                File.Move(staged, currentExe);
            }
            catch
            {
                File.Move(oldExe, currentExe); // roll back
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update: install failed", ex);
            return false;
        }

        Log.Info($"Update: installed{(restart ? ", restarting" : " (takes effect next start)")}.");
        if (restart)
            Process.Start(new ProcessStartInfo(currentExe, $"{WaitForPidArg} {Environment.ProcessId}") { UseShellExecute = false });
        return true;
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

        // A setup left over from an update that has now been installed.
        try { File.Delete(Path.Combine(Log.AppDataFolder, "updates", SetupAssetName)); }
        catch { /* ignore */ }

        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var dir = Path.GetDirectoryName(exe)!;
        foreach (var leftover in new[] { "PaperPilot.exe.old" })
        {
            try { File.Delete(Path.Combine(dir, leftover)); }
            catch { /* still locked; next start will get it */ }
        }
    }
}
