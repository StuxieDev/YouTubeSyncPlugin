using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

/// <summary>
/// Jellyfin scheduled task that triggers a full YouTube sync.
/// Appears in the dashboard under Scheduled Tasks → YouTube Sync → Sync from YouTube.
/// </summary>
public class SyncTask : IScheduledTask
{
    private readonly SyncService _syncService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncTask"/> class.</summary>
    public SyncTask(SyncService syncService, ILibraryManager libraryManager, ILogger<SyncTask> logger)
    {
        _syncService = syncService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync from YouTube";

    /// <inheritdoc />
    public string Key => "YouTubeSync";

    /// <inheritdoc />
    public string Description => "Syncs configured YouTube channels and playlists into Jellyfin by generating .strm and .nfo files.";

    /// <inheritdoc />
    public string Category => "YouTube Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("YouTube Sync task started.");
        await _syncService.SyncAllAsync(progress, cancellationToken).ConfigureAwait(false);

        // Jellyfin's file watcher adds new videos without a full scan, but durations (RuntimePostScanTask) and
        // removals are only applied by one. Queue it so new videos show their length, and reach apps such as
        // pseudo-TV schedulers, without waiting for Jellyfin's own scan schedule.
        _libraryManager.QueueLibraryScan();
        _logger.LogInformation("YouTube Sync task completed; queued a library scan to apply the changes.");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        ];
    }
}
