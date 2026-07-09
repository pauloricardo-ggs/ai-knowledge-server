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
    string? Error = null);
