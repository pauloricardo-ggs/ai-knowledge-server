using KnowledgeServer.Application;
using KnowledgeServer.Domain;

namespace KnowledgeServer.Infrastructure;

public sealed class VectorKnowledgeSearch(
    OllamaClient ollamaClient,
    QdrantClient qdrantClient,
    IWorkspaceStore workspaceStore) : IKnowledgeSearch
{
    public async Task<IReadOnlyCollection<SearchResult>> SearchAsync(
        string workspaceId,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var vector = await ollamaClient.EmbedAsync(query, cancellationToken);
            var results = await qdrantClient.SearchAsync(
                workspaceId,
                vector,
                Math.Clamp(maxResults, 1, 25),
                cancellationToken);

            if (results.Count > 0)
            {
                return results;
            }
        }
        catch
        {
            // Fall back to filesystem search while local models or Qdrant are not ready.
        }

        return await workspaceStore.SearchAsync(
            workspaceId,
            query,
            maxResults,
            cancellationToken);
    }
}

