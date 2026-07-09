namespace KnowledgeServer.Domain;

public sealed record WorkspaceRepository(
    string WorkspaceId,
    string Name,
    string RelativePath,
    string? RemoteUrl,
    string? Branch,
    string? LastCommit,
    DateTimeOffset RegisteredAt);

