namespace KnowledgeServer.Application;

public sealed record GraphifyRequest(
    string WorkspaceId,
    bool ForceFallback = false);

public sealed record GraphifyResult(
    string WorkspaceId,
    string RepositoryRoot,
    string OutputRoot,
    string Generator,
    DateTimeOffset GeneratedAt,
    int NodeCount,
    int EdgeCount,
    IReadOnlyCollection<string> Artifacts);

public interface IGraphifyService
{
    Task<GraphifyResult> GenerateAsync(
        GraphifyRequest request,
        CancellationToken cancellationToken);
}
