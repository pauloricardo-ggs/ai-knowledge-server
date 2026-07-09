using KnowledgeServer.Domain;

namespace KnowledgeServer.Application;

public interface IKnowledgeSearch
{
    Task<IReadOnlyCollection<SearchResult>> SearchAsync(
        string workspaceId,
        string query,
        int maxResults,
        CancellationToken cancellationToken);
}

