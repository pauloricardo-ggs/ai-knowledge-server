namespace KnowledgeServer.Domain;

public sealed record IndexingJob(
    string Id,
    string WorkspaceId,
    string Reason,
    string SourcePath,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? Error = null,
    IndexingProgress? Progress = null,
    IReadOnlyCollection<IndexingLogEntry>? Logs = null);

public sealed record IndexingProgress(
    string Stage,
    string Message,
    string? CurrentPath = null,
    int? TotalItems = null,
    int? ProcessedItems = null,
    IReadOnlyCollection<string>? PendingPaths = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record IndexingLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Stage,
    string Message,
    string? CurrentPath = null,
    int? TotalItems = null,
    int? ProcessedItems = null,
    IReadOnlyCollection<string>? PendingPaths = null);
