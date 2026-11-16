// Derived from madoiscool/LuaTools (MIT) — DownloadQueue.cs. Avalonia Dispatcher replaces the WPF one.
using System.Collections.ObjectModel;
using LuaTools.Core.Persistence;

namespace LuaTools.Core.Downloads;

/// <summary>
/// The app's single download scheduler. All state changes are marshalled onto the given
/// dispatcher (Avalonia's). Downloads run in parallel; installs are serialized.
/// </summary>
public sealed class DownloadQueue
{
    private readonly CacheService _cache;
    private readonly Action<Action> _onDispatcher;
    private readonly SemaphoreSlim _kick = new(0);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;

    public DownloadQueue(CacheService cache, Action<Action> onDispatcher)
    {
        _cache = cache;
        _onDispatcher = onDispatcher;
    }

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public ObservableCollection<DownloadHistoryEntry> History { get; } = new();
    public event Action? StateChanged;

    public int ActiveCount => Items.Count(i => i.IsActive);

    public void Start()
    {
        foreach (var r in _cache.GetDownloadHistory().OrderByDescending(r => r.CompletedAtMs))
            History.Add(new DownloadHistoryEntry(r));
        _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        _shutdown.Cancel();
        List<DownloadItem> active = new();
        _onDispatcher(() => active = Items.Where(i => i.IsActive).ToList());

        foreach (var item in active)
        {
            try { item.Cts.Cancel(); } catch { /* already disposed */ }
        }

        _onDispatcher(() =>
        {
            foreach (var item in active)
            {
                if (!item.IsActive) continue;
                item.Message ??= "Interrupted when the app closed.";
                item.Status = DownloadStatus.Cancelled;
                History.Insert(0, new DownloadHistoryEntry(DownloadHistoryEntry.From(item, DownloadStatus.Cancelled)));
                item.SettleCompletion(null);
            }
            PersistHistory();
        });

        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* parked on _kick */ }
        }
    }

    public DownloadItem Enqueue(DownloadJob job)
    {
        return Invoke(() =>
        {
            if (FindActiveCore(job.DedupeKey) is { } existing) return existing;
            var item = new DownloadItem(job);
            Items.Add(item);
            item.PropertyChanged += OnItemPropertyChanged;
            StateChanged?.Invoke();
            Kick();
            return item;
        });
    }

    public DownloadItem? FindActive(string dedupeKey) => Invoke(() => FindActiveCore(dedupeKey));

    private DownloadItem? FindActiveCore(string dedupeKey) =>
        Items.FirstOrDefault(i => i.IsActive &&
            string.Equals(i.Job.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase));

    public void Cancel(DownloadItem item) => Invoke(() =>
    {
        if (!item.IsActive) return;
        bool nothingRunning = item.Status is DownloadStatus.Queued or DownloadStatus.Paused;
        try { item.Cts.Cancel(); } catch { }
        if (nothingRunning)
        {
            item.PauseRequested = false;
            Finish(item, DownloadStatus.Cancelled, "Cancelled by user", null);
        }
        Kick();
    });

    public void Pause(DownloadItem item) => Invoke(() =>
    {
        if (!item.CanPause) return;
        item.PauseRequested = true;
        item.Status = DownloadStatus.Paused;
        item.BytesPerSecond = 0;
        item.Eta = null;
        try { item.Cts.Cancel(); } catch { }
        StateChanged?.Invoke();
    });

    public void Resume(DownloadItem item) => Invoke(() =>
    {
        if (!item.CanResume) return;
        item.PauseRequested = false;
        item.NeedsValidate = true;
        item.ResetCts();
        item.Status = DownloadStatus.Queued;
        StateChanged?.Invoke();
        Kick();
    });

    public DownloadItem Retry(DownloadItem item)
    {
        Invoke(() => Remove(item));
        var fresh = Enqueue(item.Job);
        if (item.Job.Kind is DownloadKind.Depot && !ReferenceEquals(fresh, item))
        {
            foreach (long id in item.CompletedDepots) fresh.CompletedDepots.Add(id);
            foreach (string f in item.CreatedFiles) fresh.CreatedFiles.Add(f);
            fresh.NeedsValidate = true;
        }
        return fresh;
    }

    public void Move(DownloadItem item, int delta) => Invoke(() =>
    {
        if (item.Status != DownloadStatus.Queued) return;
        int from = Items.IndexOf(item);
        if (from < 0) return;
        int to = Math.Clamp(from + delta, 0, Items.Count - 1);
        if (to == from) return;
        Items.Move(from, to);
        Kick();
    });

    public void Remove(DownloadItem item) => Invoke(() =>
    {
        if (item.IsActive) return;
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
        StateChanged?.Invoke();
    });

    public void ClearHistory() => Invoke(() => { History.Clear(); PersistHistory(); });

    public void RemoveHistory(DownloadHistoryEntry entry) => Invoke(() =>
    {
        if (History.Remove(entry)) PersistHistory();
    });

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status)) StateChanged?.Invoke();
    }

    private T Invoke<T>(Func<T> action) { T result = default!; _onDispatcher(() => result = action()); return result; }
    private void Invoke(Action action) => _onDispatcher(action);

    private void Kick()
    {
        try { _kick.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _kick.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _onDispatcher(() => { try { StartEligible(); } finally { tcs.TrySetResult(); } });
                await tcs.Task;
            }
            catch { /* a pump cycle failure is logged by the job itself */ }
        }
    }

    private void StartEligible()
    {
        var ready = Items.Where(i => i.Status == DownloadStatus.Queued
                                     && !i.Cts.IsCancellationRequested).ToList();
        foreach (var item in ready)
        {
            item.Status = DownloadStatus.Downloading;
            StateChanged?.Invoke();
            _ = RunItemAsync(item);
        }
    }

    private async Task RunItemAsync(DownloadItem item)
    {
        DownloadedFile? file = null;
        var ct = item.Cts.Token;

        try
        {
            long lastPostTicks = 0;
            var sink = new ProgressRelay<DownloadProgress>(p =>
            {
                long now = DateTime.UtcNow.Ticks;
                bool done = p.TotalBytes is > 0 && p.BytesRead >= p.TotalBytes.Value;
                if (!done && now - lastPostTicks < TimeSpan.TicksPerMillisecond * 100) return;
                lastPostTicks = now;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _onDispatcher(() => { try { item.ApplySample(p.BytesRead, p.TotalBytes); } finally { tcs.TrySetResult(); } });
                _ = tcs.Task;
            });

            file = await Task.Run(() => item.Job.DownloadAsync(item, sink, ct), ct);

            if (item.Job.ConfirmAsync is { } confirm)
            {
                Invoke(() => { item.Status = DownloadStatus.AwaitingConfirmation; StateChanged?.Invoke(); });

                bool proceed;
                try { proceed = await confirm(file, item, ct); }
                catch (OperationCanceledException) { proceed = false; }
                catch { proceed = false; }

                if (!proceed || ct.IsCancellationRequested)
                {
                    DeleteStaged(file.FilePath);
                    Invoke(() => Finish(item, DownloadStatus.Cancelled, "Install cancelled. Existing files left unchanged.", null));
                    return;
                }
            }

            Invoke(() => item.Status = DownloadStatus.Installing);

            await _installGate.WaitAsync(CancellationToken.None);
            JobResult result;
            try
            {
                result = await Task.Run(() => item.Job.InstallAsync(file!, item, CancellationToken.None), CancellationToken.None);
            }
            finally { _installGate.Release(); }

            Invoke(() => Finish(item, result.Ok ? DownloadStatus.Completed : DownloadStatus.Failed, result.Message, result));
        }
        catch (OperationCanceledException)
        {
            if (item.PauseRequested) return;
            if (file is not null) DeleteStaged(file.FilePath);
            Invoke(() => Finish(item, DownloadStatus.Cancelled, "Cancelled by user", null));
        }
        catch (Exception ex)
        {
            if (file is not null) DeleteStaged(file.FilePath);
            string message = ex is DownloadAbortedException ? ex.Message
                : "Download failed. Check your connection and try again.";
            var status = ex is DownloadAbortedException { IsCancellation: true }
                ? DownloadStatus.Cancelled : DownloadStatus.Failed;
            Invoke(() => Finish(item, status, message, null));
        }
        finally
        {
            Kick();
        }
    }

    private void Finish(DownloadItem item, DownloadStatus status, string? message, JobResult? result)
    {
        if (!item.IsActive) return;
        item.Message = message;
        item.Status = status;
        item.RecordInstalledPath(result?.InstalledPath);

        History.Insert(0, new DownloadHistoryEntry(DownloadHistoryEntry.From(item, status)));
        while (History.Count > 100) History.RemoveAt(History.Count - 1);
        PersistHistory();

        item.SettleCompletion(result);
        try { item.Job.OnFinished?.Invoke(item, result); }
        catch { /* a bad continuation cannot kill the pump */ }
        StateChanged?.Invoke();

        if (status == DownloadStatus.Completed) _ = AutoDismissAsync(item);
    }

    private async Task AutoDismissAsync(DownloadItem item)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), _shutdown.Token); }
        catch (OperationCanceledException) { return; }

        try { Invoke(() => { if (item.Status == DownloadStatus.Completed && Items.Contains(item)) Remove(item); }); }
        catch { /* best effort */ }
    }

    private void PersistHistory()
    {
        try { _cache.SaveDownloadHistory(History.Select(h => h.Record)); }
        catch { /* best effort */ }
    }

    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
