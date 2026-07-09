namespace KnowledgeServer.Application;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    public string RootPath { get; set; } = "/app/workspaces";

    public string GraphifyCommand { get; set; } = "graphify";

    public string GraphifyArguments { get; set; } = "\"{InputRoot}\" --output \"{GraphRoot}\"";
}
