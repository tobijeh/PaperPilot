using System.IO;
using System.Windows.Threading;
using PaperPilot.Diagnostics;

namespace PaperPilot.Core;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Ready,
    Failed,
    NotAvailable,
}

/// <summary>
/// Wirl-style updates: check on launch and once a day, download in the background, install
/// on "Restart to update" or when Paper Pilot quits. Never touches the network while Paper
/// Pilot is on (listening/scrolling); a due check waits until it's off.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryWhileOn = TimeSpan.FromMinutes(10);

    private readonly Func<bool> _isBusy;
    private readonly DispatcherTimer _timer = new();
    private bool _running;
    private bool _applied; // the update has been handed to the installer; never twice

    public UpdateService(Func<bool> isBusy)
    {
        _isBusy = isBusy;
        _timer.Tick += async (_, _) => await TimerTickAsync();
        if (!Updater.CanSelfUpdate)
        {
            Status = UpdateStatus.NotAvailable;
            return;
        }
        // A finished download from last time that never got installed.
        try
        {
            var staged = Updater.StagedPath;
            if (File.Exists(staged)) File.Delete(staged); // we can't verify its version; fetch fresh
        }
        catch (Exception ex)
        {
            Log.Warn($"Update: could not remove an old download ({ex.Message}).");
        }
        Schedule(FirstCheck);
    }

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public UpdateInfo? Available { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastChecked { get; private set; }

    /// <summary>Raised on the UI thread whenever status or progress changes.</summary>
    public event Action? Changed;

    private void Schedule(TimeSpan after)
    {
        _timer.Stop();
        _timer.Interval = after;
        _timer.Start();
    }

    private async Task TimerTickAsync()
    {
        _timer.Stop();
        if (_isBusy())
        {
            Schedule(RetryWhileOn);
            return;
        }
        await CheckAsync(userRequested: false);
        Schedule(Daily);
    }

    /// <summary>Checks now and, if there's a newer version, downloads it in the background.</summary>
    public async Task CheckAsync(bool userRequested)
    {
        if (_running || Status == UpdateStatus.NotAvailable) return;
        if (Status == UpdateStatus.Ready && !userRequested) return;
        if (_isBusy())
        {
            Log.Info("Update: check postponed while Paper Pilot is on.");
            return;
        }

        _running = true;
        try
        {
            Set(UpdateStatus.Checking);
            var info = await Updater.CheckAsync();
            LastChecked = DateTime.Now;
            if (info is null)
            {
                if (Status != UpdateStatus.Ready) Set(UpdateStatus.UpToDate);
                return;
            }
            if (Available?.Version == info.Version && Status == UpdateStatus.Ready) return;

            Available = info;
            Progress = 0;
            Set(UpdateStatus.Downloading);
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            await Updater.DownloadAsync(info, progress);
            Set(UpdateStatus.Ready);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update: {ex.Message}");
            Error = ex is System.Net.Http.HttpRequestException or TaskCanceledException
                ? "Couldn't reach GitHub. Paper Pilot will try again later."
                : ex.Message;
            Set(UpdateStatus.Failed);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Installs the downloaded version and restarts into it. Returns false if it couldn't.</summary>
    public bool RestartToUpdate()
    {
        if (_applied || Status != UpdateStatus.Ready) return false;
        _applied = Updater.ApplyStaged(restart: true);
        return _applied;
    }

    /// <summary>Called when Paper Pilot quits: a downloaded update is put in place for next time.</summary>
    public void InstallOnExit()
    {
        if (!_applied && Status == UpdateStatus.Ready) _applied = Updater.ApplyStaged(restart: false);
    }

    private void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
