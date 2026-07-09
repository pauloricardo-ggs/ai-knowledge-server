namespace KnowledgeServer.Application;

public sealed class QdrantOptions
{
    public const string SectionName = "Qdrant";

    public string Endpoint { get; set; } = "http://localhost:6333";
}

