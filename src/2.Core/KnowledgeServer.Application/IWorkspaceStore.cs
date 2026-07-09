using KnowledgeServer.Domain;

namespace KnowledgeServer.Application;

public interface IWorkspaceStore
{
    Task<IReadOnlyCollection<Workspace>> ListWorkspacesAsync(CancellationToken cancellationToken);

    Task<Workspace> GetOrCreateWorkspaceAsync(string workspaceId, CancellationToken cancellationToken);

    Task<WorkspaceDocument> SaveDocumentAsync(
        string workspaceId,
        string category,
        string fileName,
        Stream content,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<WorkspaceDocument>> ListDocumentsAsync(
        string workspaceId,
        CancellationToken cancellationToken);

    Task<WorkspaceRepository> RegisterRepositoryAsync(
        string workspaceId,
        string name,
        string relativePath,
        string? remoteUrl,
        string? branch,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<WorkspaceRepository>> ListRepositoriesAsync(
        string workspaceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<SearchResult>> SearchAsync(
        string workspaceId,
        string query,
        int maxResults,
        CancellationToken cancellationToken);

    Task<IndexingJob> EnqueueIndexingJobAsync(
        string workspaceId,
        string reason,
        string sourcePath,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<IndexingJob>> ListIndexingJobsAsync(
        string workspaceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<IndexingJob>> ListQueuedIndexingJobsAsync(
        CancellationToken cancellationToken);

    Task<IndexingJob?> UpdateIndexingJobStatusAsync(
        string workspaceId,
        string jobId,
        string status,
        string? error,
        CancellationToken cancellationToken);

    Task<IndexingJob?> UpdateIndexingJobProgressAsync(
        string workspaceId,
        string jobId,
        IndexingProgress progress,
        CancellationToken cancellationToken);
}
