namespace KnowledgeServer.Domain;

public sealed record Workspace(
    string Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAt);

