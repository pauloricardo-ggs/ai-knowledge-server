namespace KnowledgeServer.Domain;

public sealed record KnowledgeChunk(
    string Id,
    string WorkspaceId,
    string RelativePath,
    string SourceKind,
    string Content,
    int StartLine,
    int EndLine);

