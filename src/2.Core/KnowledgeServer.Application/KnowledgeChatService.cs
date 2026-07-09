using KnowledgeServer.Domain;

namespace KnowledgeServer.Application;

public sealed class KnowledgeChatService(
    IKnowledgeGenerator knowledgeGenerator,
    IKnowledgeSearch knowledgeSearch)
{
    public async Task<ChatResponse> AskAsync(
        string workspaceId,
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        var maxResults = request.MaxResults <= 0 ? 5 : Math.Min(request.MaxResults, 20);
        var answer = await knowledgeGenerator.AnswerAsync(
            workspaceId,
            request.Message,
            maxResults,
            cancellationToken);

        if (answer.References.Count > 0)
        {
            return new ChatResponse(answer.Answer, answer.References);
        }

        var fallback = await knowledgeSearch.SearchAsync(
            workspaceId,
            request.Message,
            maxResults,
            cancellationToken);

        return fallback.Count == 0
            ? new ChatResponse("Ainda não encontrei documentos indexados que respondam a essa pergunta neste workspace.", [])
            : new ChatResponse(answer.Answer, fallback);
    }
}
