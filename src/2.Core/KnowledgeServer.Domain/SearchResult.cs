namespace KnowledgeServer.Domain;

public sealed record SearchResult(
    string WorkspaceId,
    string RelativePath,
    string Snippet,
    int Score);

