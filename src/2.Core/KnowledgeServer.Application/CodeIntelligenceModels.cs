namespace KnowledgeServer.Application;

public sealed record CodeIntelligenceRequest(
    string WorkspaceId,
    bool IncludeReferences = true,
    bool IncludeCallGraph = true,
    bool IncludeEndpoints = true,
    bool IncludeRelatedTests = true);

public sealed record CodeIntelligenceResult(
    string WorkspaceId,
    string RepositoryRoot,
    string OutputRoot,
    string Analyzer,
    DateTimeOffset GeneratedAt,
    int FileCount,
    int SymbolCount,
    IReadOnlyCollection<string> Artifacts);

public interface ICodeIntelligenceService
{
    Task<CodeIntelligenceResult> GenerateAsync(
        CodeIntelligenceRequest request,
        CancellationToken cancellationToken);
}
