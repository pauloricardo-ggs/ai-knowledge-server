namespace KnowledgeServer.Application;

public static class WorkspaceLayout
{
    public const string InputsRootName = "inputs";
    public const string RepositoriesRootName = "repositories";
    public const string DocumentsRootName = "documents";

    public static string InputsRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, InputsRootName);

    public static string RepositoriesRoot(string workspaceRoot)
        => Path.Combine(InputsRoot(workspaceRoot), RepositoriesRootName);

    public static string DocumentsRoot(string workspaceRoot)
        => Path.Combine(InputsRoot(workspaceRoot), DocumentsRootName);

    public static string RawDocumentsRoot(string workspaceRoot)
        => Path.Combine(DocumentsRoot(workspaceRoot), "raw");

    public static string GraphsRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, "graphs");

    public static string RoslynRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, "roslyn");

    public static string SummariesRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, "summaries");

    public static string EmbeddingsRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, "embeddings");

    public static string JobsRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, "cache", "jobs");

    public static string LogsRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, "logs");
}