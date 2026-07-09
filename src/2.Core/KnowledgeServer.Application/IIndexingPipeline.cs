using KnowledgeServer.Domain;

namespace KnowledgeServer.Application;

public interface IIndexingPipeline
{
    Task IndexWorkspaceAsync(
        string workspaceId,
    string jobId,
        string reason,
    Func<IndexingProgress, Task> reportProgressAsync,
        CancellationToken cancellationToken);
}

