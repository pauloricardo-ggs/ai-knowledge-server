using System.Collections.Concurrent;
using KnowledgeServer.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeServer.Worker;

public sealed class Worker(
    IWorkspaceStore workspaceStore,
    IIndexingPipeline indexingPipeline,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JobPollingInterval = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, PendingChange> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset lastJobPoll = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rootPath = Path.GetFullPath(workspaceOptions.Value.RootPath);
        Directory.CreateDirectory(rootPath);

        using var watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
        };

        watcher.Created += (_, args) => TrackChange(rootPath, args.FullPath, "created");
        watcher.Changed += (_, args) => TrackChange(rootPath, args.FullPath, "changed");
        watcher.Deleted += (_, args) => TrackChange(rootPath, args.FullPath, "deleted");
        watcher.Renamed += (_, args) => TrackChange(rootPath, args.FullPath, "renamed");

        logger.LogInformation("Watching workspace root {WorkspaceRoot}", rootPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            await FlushStableChangesAsync(stoppingToken);
            await ProcessQueuedJobsAsync(stoppingToken);
            await Task.Delay(1000, stoppingToken);
        }
    }

    private void TrackChange(string rootPath, string path, string reason)
    {
        if (!TryGetWorkspaceId(rootPath, path, out var workspaceId) || IsIgnored(path))
        {
            return;
        }

        pendingChanges.AddOrUpdate(
            workspaceId,
            _ => new PendingChange(workspaceId, reason, path, DateTimeOffset.UtcNow),
            (_, existing) => existing with
            {
                Reason = MergeReason(existing.Reason, reason),
                SourcePath = path,
                LastSeenAt = DateTimeOffset.UtcNow
            });
    }

    private async Task FlushStableChangesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var change in pendingChanges.Values)
        {
            if (now - change.LastSeenAt < DebounceWindow)
            {
                continue;
            }

            if (!pendingChanges.TryRemove(change.WorkspaceId, out var stableChange))
            {
                continue;
            }

            var job = await workspaceStore.EnqueueIndexingJobAsync(
                stableChange.WorkspaceId,
                $"filesystem-{stableChange.Reason}",
                stableChange.SourcePath,
                cancellationToken);

            logger.LogInformation(
                "Queued indexing job {JobId} for workspace {WorkspaceId} after {Reason}",
                job.Id,
                job.WorkspaceId,
                job.Reason);
        }
    }

    private async Task ProcessQueuedJobsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastJobPoll < JobPollingInterval)
        {
            return;
        }

        lastJobPoll = now;

        var jobs = await workspaceStore.ListQueuedIndexingJobsAsync(cancellationToken);
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await workspaceStore.UpdateIndexingJobStatusAsync(
                job.WorkspaceId,
                job.Id,
                "running",
                null,
                cancellationToken);

            try
            {
                logger.LogInformation(
                    "Processing indexing job {JobId} for workspace {WorkspaceId}",
                    job.Id,
                    job.WorkspaceId);

                await indexingPipeline.IndexWorkspaceAsync(
                    job.WorkspaceId,
                    job.Reason,
                    cancellationToken);

                await workspaceStore.UpdateIndexingJobStatusAsync(
                    job.WorkspaceId,
                    job.Id,
                    "completed",
                    null,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Indexing job {JobId} for workspace {WorkspaceId} failed",
                    job.Id,
                    job.WorkspaceId);

                await workspaceStore.UpdateIndexingJobStatusAsync(
                    job.WorkspaceId,
                    job.Id,
                    "failed",
                    ex.Message,
                    cancellationToken);
            }
        }
    }

    private static bool TryGetWorkspaceId(string rootPath, string path, out string workspaceId)
    {
        workspaceId = string.Empty;

        var relative = Path.GetRelativePath(rootPath, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return false;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length == 0 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        workspaceId = segments[0];
        return true;
    }

    private static bool IsIgnored(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is ".git" or "bin" or "obj" or "node_modules")
            || segments.Any(segment => segment is "cache" or "logs" or "embeddings");
    }

    private static string MergeReason(string current, string next)
    {
        return current.Contains(next, StringComparison.OrdinalIgnoreCase)
            ? current
            : $"{current},{next}";
    }

    private sealed record PendingChange(
        string WorkspaceId,
        string Reason,
        string SourcePath,
        DateTimeOffset LastSeenAt);
}
