using KnowledgeServer.Domain;

namespace KnowledgeServer.Application;

public sealed record KnowledgeAnswer(
    string Answer,
    IReadOnlyCollection<SearchResult> References,
    string Provider);

