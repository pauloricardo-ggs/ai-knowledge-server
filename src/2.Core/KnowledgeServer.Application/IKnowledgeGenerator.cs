namespace KnowledgeServer.Application;

public interface IKnowledgeGenerator
{
    Task<KnowledgeAnswer> AnswerAsync(
        string workspaceId,
        string question,
        int maxResults,
        CancellationToken cancellationToken);
}

