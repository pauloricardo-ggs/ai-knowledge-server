namespace KnowledgeServer.Application;

public interface IIndexingPipeline
{
    Task IndexWorkspaceAsync(
        string workspaceId,
        string reason,
        CancellationToken cancellationToken);
}

