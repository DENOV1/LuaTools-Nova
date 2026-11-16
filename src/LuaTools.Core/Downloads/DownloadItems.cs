// Derived from madoiscool/LuaTools (MIT) — Services/Downloads/* (queue scheduler, job model, history).
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LuaTools.Core.Persistence;

namespace LuaTools.Core.Downloads;

public enum DownloadKind { Manifest, Dlc, DenuvoManifest, DenuvoFix, Depot, Tool }

public enum DownloadStatus
{
    Queued, Downloading, AwaitingConfirmation, Installing, Paused, Verifying, Completed, Failed, Cancelled,
}

public sealed class DownloadAbortedException(string message, bool isCancellation = false) : Exception(message)
{
    public bool IsCancellation { get; } = isCancellation;
}

public sealed record JobResult(bool Ok, string? Message, string? InstalledPath = null);

public sealed record DownloadedFile(string FilePath, string FileName);

public readonly record struct DownloadProgress(long BytesRead, long? TotalBytes);

public sealed class ProgressRelay<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

/// <summary>One unit of work for the queue. The queue is a pure scheduler.</summary>
public sealed record DownloadJob(
    DownloadKind Kind,
    string DedupeKey,
    long AppId,
    string Title,
    string SubTitle,
    string? CoverPath,
    Func<DownloadItem, IProgress<DownloadProgress>, CancellationToken, Task<DownloadedFile>> DownloadAsync,
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<JobResult>> InstallAsync,
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? ConfirmAsync = null,
    Action<DownloadItem, JobResult?>? OnFinished = null,
    Action? OnReveal = null,
    string? OutputPath = null);

public partial class DownloadItem : ObservableObject
{
    private readonly TaskCompletionSource<JobResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(0.5);
    private const int MaxSamples = 64;

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public DownloadItem(DownloadJob job)
    {
        Job = job;
        Cts = new CancellationTokenSource();
        EnqueuedAt = DateTimeOffset.Now;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DownloadJob Job { get; }
    public DateTimeOffset EnqueuedAt { get; }
    public CancellationTokenSource Cts { get; private set; }
    public bool PauseRequested { get; set; }
    public bool NeedsValidate { get; set; }

    public void ResetCts()
    {
        try { Cts.Dispose(); } catch { /* already disposed */ }
        Cts = new CancellationTokenSource();
    }

    public Task<JobResult?> Completion => _completion.Task;

    public string Title => Job.Title;
    public string SubTitle => Job.SubTitle;
    public string? CoverPath => Job.CoverPath;
    public long AppId => Job.AppId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning), nameof(CanCancel), nameof(CanRetry),
        nameof(CanRemove), nameof(CanReorder), nameof(ShowProgress), nameof(NeedsAction), nameof(CanPause),
        nameof(CanResume))]
    private DownloadStatus _status = DownloadStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    private long _bytesRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(IsIndeterminate))]
    private long? _totalBytes;

    [ObservableProperty] private double _bytesPerSecond;
    [ObservableProperty] private TimeSpan? _eta;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _detail;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public HashSet<long> CompletedDepots { get; } = new();
    public HashSet<string> CreatedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    private string? _installedPath;

    public string? RevealPath => _installedPath ?? Job.OutputPath;
    public bool CanCopyAppId => AppId > 0;
    public bool CanShowInFolder => !string.IsNullOrWhiteSpace(RevealPath);

    public void RecordInstalledPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _installedPath = path;
        OnPropertyChanged(nameof(RevealPath));
        OnPropertyChanged(nameof(CanShowInFolder));
    }

    public bool IsIndeterminate => TotalBytes is not > 0;
    public double Percent => TotalBytes is > 0 ? BytesRead * 100d / TotalBytes.Value : 0;

    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading
        or DownloadStatus.AwaitingConfirmation or DownloadStatus.Installing
        or DownloadStatus.Paused or DownloadStatus.Verifying;
    public bool IsRunning => Status is DownloadStatus.Downloading or DownloadStatus.Installing
        or DownloadStatus.Verifying;
    public bool ShowProgress => Status is DownloadStatus.Downloading or DownloadStatus.Installing
        or DownloadStatus.Paused or DownloadStatus.Verifying;
    public bool NeedsAction => Status is DownloadStatus.AwaitingConfirmation;
    public bool CanCancel => IsActive;
    public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Cancelled;
    public bool CanRemove => !IsActive;
    public bool CanReorder => Status is DownloadStatus.Queued;
    public bool CanPause => Status is (DownloadStatus.Downloading or DownloadStatus.Verifying)
        && Job.Kind is DownloadKind.Depot;
    public bool CanResume => Status is DownloadStatus.Paused;

    public string StatusLabel => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.AwaitingConfirmation => "Waiting for you",
        DownloadStatus.Installing => "Installing",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Verifying => "Verifying…",
        DownloadStatus.Completed => "Done",
        DownloadStatus.Failed => "Failed",
        _ => "Cancelled",
    };

    public string SizeLabel
    {
        get
        {
            if (BytesRead <= 0 && TotalBytes is not > 0) return "";
            if (TotalBytes is > 0)
                return $"{ByteFormat.Size(BytesRead)} of {ByteFormat.Size(TotalBytes.Value)}";
            return ByteFormat.Size(BytesRead);
        }
    }

    public string RateLabel => Status is DownloadStatus.Downloading ? ByteFormat.Rate(BytesPerSecond) : "";

    public string EtaLabel
    {
        get
        {
            if (Status is not DownloadStatus.Downloading || Eta is not { } eta) return "";
            string d = ByteFormat.Duration(eta);
            return d.Length == 0 ? "" : $"{d} left";
        }
    }

    public void ApplySample(long bytesRead, long? total)
    {
        BytesRead = bytesRead;
        TotalBytes = total;

        long now = UtcNow().Ticks;
        _samples.Enqueue((now, bytesRead));
        while (_samples.Count > MaxSamples) _samples.Dequeue();

        while (_samples.Count > 2
               && now - _samples.Peek().Ticks > RateWindow.Ticks
               && now - _samples.ElementAt(1).Ticks >= MinSpan.Ticks)
            _samples.Dequeue();

        if (_samples.Count < 2) return;

        var (oldTicks, oldBytes) = _samples.Peek();
        double seconds = (now - oldTicks) / (double)TimeSpan.TicksPerSecond;
        if (seconds < MinSpan.TotalSeconds) return;

        double rate = (bytesRead - oldBytes) / seconds;
        BytesPerSecond = rate;
        Eta = rate > 0 && total is > 0 && total.Value > bytesRead
            ? TimeSpan.FromSeconds((total.Value - bytesRead) / rate)
            : null;
    }

    public void ResetMetrics()
    {
        _samples.Clear();
        BytesRead = 0;
        TotalBytes = null;
        BytesPerSecond = 0;
        Eta = null;
        Message = null;
    }

    public void SettleCompletion(JobResult? result) => _completion.TrySetResult(result);
}

public sealed record DownloadHistoryEntry(DownloadHistoryRecord Record)
{
    public DownloadStatus Status => Enum.TryParse<DownloadStatus>(Record.Status, out var s) ? s : DownloadStatus.Completed;
    public bool Failed => Status is DownloadStatus.Failed;
    public string SizeLabel => Record.Bytes > 0 ? ByteFormat.Size(Record.Bytes) : "";
    public string StatusLabel => Status switch
    {
        DownloadStatus.Completed => "Done",
        DownloadStatus.Failed => "Failed",
        _ => "Cancelled",
    };
    public bool CanCopyAppId => Record.AppId > 0;
    public bool CanShowInFolder => !string.IsNullOrWhiteSpace(Record.RevealPath);
    public string WhenLabel => DateTimeOffset.FromUnixTimeMilliseconds(Record.CompletedAtMs).LocalDateTime.ToString("g");

    public static DownloadHistoryRecord From(DownloadItem item, DownloadStatus status) => new(
        item.Id, item.Job.Kind.ToString(), item.AppId, item.Title, item.SubTitle, item.BytesRead,
        status.ToString(), item.Message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), item.RevealPath);
}

public static class ByteFormat
{
    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return gb.ToString("0.##") + " GB";
        double mb = bytes / 1024d / 1024d;
        if (mb >= 1) return mb.ToString("0.#") + " MB";
        double kb = bytes / 1024d;
        if (kb >= 1) return kb.ToString("0") + " KB";
        return bytes.ToString("0") + " B";
    }

    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0) return "";
        return Size((long)bytesPerSecond) + "/s";
    }

    public static string Duration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero || t.TotalDays >= 1) return "";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))}s";
    }
}
