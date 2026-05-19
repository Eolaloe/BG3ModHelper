using System.Collections.Concurrent;
using BG3ModHelper.Models;

namespace BG3ModHelper.Services;

public enum DownloadJobKind { Nxm, AutoUpdate, LocalArchive }

public sealed class DownloadJob
{
    public required DownloadJobKind Kind    { get; init; }
    public required string          Label   { get; init; }
    public required Func<Task>      Execute { get; init; }
    public NxmQueueItem?            NxmItem { get; init; }
}

/// <summary>
/// Single serialized worker queue for ALL download/install operations.
/// Replaces NxmDownloadQueue — NXM, auto-update, and local-archive jobs
/// all run through this queue so they never overlap.
/// </summary>
public sealed class UnifiedDownloadQueue
{
    public static UnifiedDownloadQueue Instance { get; } = new();

    private readonly ConcurrentQueue<DownloadJob>  _queue      = new();
    private readonly Dictionary<string, DateTime>  _nxmRecent  = new();
    private readonly object                        _recentLock = new();
    private readonly SemaphoreSlim                 _workerLock = new(1, 1);

    private const int DedupeSeconds = 5;
    private Func<NxmQueueItem, Task>? _nxmHandler;

    private UnifiedDownloadQueue() { }

    public void SetNxmHandler(Func<NxmQueueItem, Task> handler) => _nxmHandler = handler;

    public int PendingCount => _queue.Count;

    // Generic progress event — fired for every download type so the main window
    // can show a unified progress bar without knowing which path triggered it.
    public event Action<DownloadProgress>? OnProgress;
    public void ReportProgress(DownloadProgress p) => OnProgress?.Invoke(p);

    // NXM-specific events (UpdateNotificationViewModel subscribes for WebView tracking)
    public event Action<NxmUrl, int>?                    OnNxmQueued;
    public event Action<NxmQueueItem, DownloadProgress>? OnNxmProgress;
    public event Action<NxmQueueItem, bool, string?>?    OnNxmCompleted;

    public void ReportNxmProgress(NxmQueueItem item, DownloadProgress p)
    {
        OnNxmProgress?.Invoke(item, p);
        OnProgress?.Invoke(p);
    }

    public void NotifyNxmCompleted(NxmQueueItem item, bool success, string? error = null) =>
        OnNxmCompleted?.Invoke(item, success, error);

    /// <summary>
    /// Enqueues an nxm:// download. Returns false if blocked by 5-second dedupe guard.
    /// </summary>
    public bool EnqueueNxm(NxmUrl url, string rawUrl)
    {
        lock (_recentLock)
        {
            if (_nxmRecent.TryGetValue(rawUrl, out var last) &&
                (DateTime.UtcNow - last).TotalSeconds < DedupeSeconds)
            {
                Logger.Info($"UnifiedDownloadQueue: NXM dedupe — {rawUrl}");
                return false;
            }
            _nxmRecent[rawUrl] = DateTime.UtcNow;
        }

        var item = new NxmQueueItem(url, rawUrl, DateTime.UtcNow);
        var job = new DownloadJob
        {
            Kind    = DownloadJobKind.Nxm,
            Label   = $"NXM: {url.Game}/mods/{url.NexusModId}/files/{url.NexusFileId}",
            NxmItem = item,
            Execute = async () =>
            {
                if (_nxmHandler is null)
                {
                    Logger.Warn("UnifiedDownloadQueue: no NXM handler registered — dropping");
                    return;
                }
                await _nxmHandler(item);
            }
        };

        _queue.Enqueue(job);
        Logger.Info($"UnifiedDownloadQueue: NXM enqueued {url.Game}/mods/{url.NexusModId}/files/{url.NexusFileId} (queue: {_queue.Count})");
        OnNxmQueued?.Invoke(url, _queue.Count);
        _ = Task.Run(WorkerLoopAsync);
        return true;
    }

    /// <summary>
    /// Enqueues a generic job (AutoUpdate or LocalArchive).
    /// </summary>
    public void Enqueue(DownloadJob job)
    {
        _queue.Enqueue(job);
        _ = Task.Run(WorkerLoopAsync);
    }

    // Fired when a job begins/ends — allows UI to show a busy indicator for all job types
    public event Action<DownloadJob>? OnJobStarted;
    public event Action<DownloadJob>? OnJobFinished;

    /// <summary>The job currently executing, or null when the queue is idle.</summary>
    public DownloadJob? CurrentJob { get; private set; }

    private async Task WorkerLoopAsync()
    {
        if (!await _workerLock.WaitAsync(0))
            return;

        try
        {
            while (_queue.TryDequeue(out var job))
            {
                CurrentJob = job;
                OnJobStarted?.Invoke(job);
                try
                {
                    await job.Execute();
                }
                catch (Exception ex)
                {
                    Logger.Error($"UnifiedDownloadQueue: '{job.Label}' failed — {ex.Message}");
                }
                finally
                {
                    CurrentJob = null;
                    OnJobFinished?.Invoke(job);
                }
            }
        }
        finally
        {
            _workerLock.Release();
        }
    }
}

/// <summary>Queue item for an nxm:// download — original URL + parsed form + enqueue time.</summary>
public sealed record NxmQueueItem(NxmUrl Url, string RawUrl, DateTime EnqueuedAt);
