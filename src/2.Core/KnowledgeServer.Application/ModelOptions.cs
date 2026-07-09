namespace KnowledgeServer.Application;

public sealed class ModelOptions
{
    public const string SectionName = "Models";

    public string ChatModel { get; set; } = "llama3.2:3b";

    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    public bool AutoPull { get; set; } = true;
}

