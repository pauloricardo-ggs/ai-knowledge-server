namespace KnowledgeServer.Domain;

public sealed record WorkspaceDocument(
    string WorkspaceId,
    string RelativePath,
    string FileName,
    string Category,
    long SizeBytes,
    DateTimeOffset LastModifiedAt);

