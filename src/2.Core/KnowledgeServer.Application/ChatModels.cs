using KnowledgeServer.Domain;

namespace KnowledgeServer.Application;

public sealed record ChatRequest(string Message, int MaxResults = 5);

public sealed record ChatResponse(
    string Answer,
    IReadOnlyCollection<SearchResult> References);

