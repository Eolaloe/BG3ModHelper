using System.Collections.Concurrent;
using BG3MM_UpdateHelper.Models;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// FIFO queue + single worker for nxm:// download requests.
/// One worker runs at a time — concurrent downloads not supported.
/// 5-second dedupe guard prevents double-clicks from queuing the same URL twice.
/// </summary>
public sealed class NxmDownloadQueue
{
    public static NxmDownloadQueue Instance { get; } = new();

    private readonly ConcurrentQueue<NxmQueueItem> _queue       = new();
    private readonly Dictionary<string, DateTime>  _recent      = new();
    private readonly object                        _recentLock  = new();
    private readonly SemaphoreSlim                 _workerLock  = new(1, 1);

    private const int    DedupeSeconds = 5;
    private       Func<NxmQueueItem, Task>? _handler;

    private NxmDownloadQueue() { }

    /// <summary>
    /// Sets the worker handler — called once during app startup.
    /// The handler is responsible for actually downloading + installing a single item.
    /// </summary>
    public void SetHandler(Func<NxmQueueItem, Task> handler) => _handler = handler;

    public int  PendingCount => _queue.Count;

    /// <summary>
    /// Enqueues a validated nxm URL. Returns false if blocked by dedupe guard.
    /// </summary>
    public bool Enqueue(NxmUrl url, string rawUrl)
    {
        lock (_recentLock)
        {
            if (_recent.TryGetValue(rawUrl, out var last) &&
                (DateTime.UtcNow - last).TotalSeconds < DedupeSeconds)
            {
                Logger.Info($"NxmDownloadQueue: dedupe — ignoring duplicate {rawUrl}");
                return false;
            }
            _recent[rawUrl] = DateTime.UtcNow;
        }

        _queue.Enqueue(new NxmQueueItem(url, rawUrl, DateTime.UtcNow));

        var queueSize = _queue.Count;
        Logger.Info($"NxmDownloadQueue: enqueued {url.Game}/mods/{url.NexusModId}/files/{url.NexusFileId} (queue size: {queueSize})");

        // Notify queue listener (UI layer)
        OnQueued?.Invoke(url, queueSize);

        // Fire worker if idle (non-blocking)
        _ = Task.Run(WorkerLoopAsync);

        return true;
    }

    /// <summary>
    /// Fired when a new item is enqueued.
    /// Args: (url, totalQueueSize including the new item).
    /// </summary>
    public event Action<NxmUrl, int>? OnQueued;

    /// <summary>
    /// Worker loop — drains the queue serially. Multiple Enqueue calls
    /// converge here but only one loop runs at a time (semaphore).
    /// </summary>
    private async Task WorkerLoopAsync()
    {
        if (!await _workerLock.WaitAsync(0))
            return;

        try
        {
            while (_queue.TryDequeue(out var item))
            {
                if (_handler is null)
                {
                    Logger.Warn("NxmDownloadQueue: no handler registered — dropping item");
                    continue;
                }

                try
                {
                    await _handler(item);
                }
                catch (Exception ex)
                {
                    Logger.Error($"NxmDownloadQueue: handler error for {item.RawUrl} — {ex.Message}");
                }
            }
        }
        finally
        {
            _workerLock.Release();
        }
    }
}

/// <summary>Queue item — original URL + parsed form + enqueue time.</summary>
public sealed record NxmQueueItem(NxmUrl Url, string RawUrl, DateTime EnqueuedAt);
