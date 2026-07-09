using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeServer.Application;
using KnowledgeServer.Domain;
using KnowledgeServer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddKnowledgeServerInfrastructure(builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowAnyOrigin();
    });
});

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();

app.MapGet("/", () => Results.Ok(new
{
    service = "AI Knowledge Server",
    status = "running",
    endpoints = new[]
    {
        "/health",
        "/workspaces",
        "/workspaces/{workspaceId}/documents",
        "/workspaces/{workspaceId}/repositories",
        "/workspaces/{workspaceId}/chat",
        "/workspaces/{workspaceId}/jobs",
        "/ui",
        "/v1/models",
        "/v1/chat/completions",
        "/mcp",
        "/mcp/info"
    }
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    checkedAt = DateTimeOffset.UtcNow
}));

app.MapGet("/workspaces", async (
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var workspaces = await workspaceStore.ListWorkspacesAsync(cancellationToken);
    return Results.Ok(workspaces);
});

app.MapPost("/workspaces/{workspaceId}", async (
    string workspaceId,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
    return Results.Created($"/workspaces/{workspace.Id}", workspace);
});

app.MapGet("/workspaces/{workspaceId}/documents", async (
    string workspaceId,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var documents = await workspaceStore.ListDocumentsAsync(workspaceId, cancellationToken);
    return Results.Ok(documents);
});

app.MapGet("/workspaces/{workspaceId}/repositories", async (
    string workspaceId,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var repositories = await workspaceStore.ListRepositoriesAsync(workspaceId, cancellationToken);
    return Results.Ok(repositories);
});

app.MapPost("/workspaces/{workspaceId}/repositories", async (
    string workspaceId,
    RegisterRepositoryRequest request,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest(new { message = "Repository name is required." });
    }

    var repository = await workspaceStore.RegisterRepositoryAsync(
        workspaceId,
        request.Name,
        request.RelativePath ?? Path.Combine("repositories", request.Name),
        request.RemoteUrl,
        request.Branch,
        cancellationToken);

    return Results.Created($"/workspaces/{workspaceId}/repositories/{repository.Name}", repository);
});

app.MapPost("/workspaces/{workspaceId}/documents", async (
    string workspaceId,
    IFormFile file,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken,
    string category = "raw") =>
{
    await using var stream = file.OpenReadStream();
    var document = await workspaceStore.SaveDocumentAsync(
        workspaceId,
        category,
        file.FileName,
        stream,
        cancellationToken);

    return Results.Created($"/workspaces/{workspaceId}/documents/{document.RelativePath}", document);
})
.DisableAntiforgery();

app.MapPost("/workspaces/{workspaceId}/chat", async (
    string workspaceId,
    ChatRequest request,
    KnowledgeChatService chatService,
    CancellationToken cancellationToken) =>
{
    var response = await chatService.AskAsync(workspaceId, request, cancellationToken);
    return Results.Ok(response);
});

app.MapPost("/workspaces/{workspaceId}/jobs/reindex", async (
    string workspaceId,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken,
    string reason = "manual-reindex") =>
{
    var job = await workspaceStore.EnqueueIndexingJobAsync(
        workspaceId,
        reason,
        ".",
        cancellationToken);

    return Results.Accepted($"/workspaces/{workspaceId}/jobs/{job.Id}", job);
});

app.MapGet("/workspaces/{workspaceId}/jobs", async (
    string workspaceId,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var jobs = await workspaceStore.ListIndexingJobsAsync(workspaceId, cancellationToken);
    return Results.Ok(jobs);
});

app.MapGet("/workspaces/{workspaceId}/files", async (
    string workspaceId,
    string? path,
    bool includeHidden,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
    var targetPath = TryResolveWorkspacePath(workspace.RootPath, path);
    if (targetPath is null)
    {
        return Results.BadRequest(new { message = "Invalid workspace path." });
    }

    if (File.Exists(targetPath))
    {
        return Results.BadRequest(new { message = "The requested path points to a file. Use the content endpoint instead." });
    }

    if (!Directory.Exists(targetPath))
    {
        return Results.NotFound(new { message = "Directory not found." });
    }

    var currentPath = ToWorkspaceRelativePath(workspace.RootPath, targetPath);
    var entries = Directory
        .EnumerateFileSystemEntries(targetPath)
        .Where(entry => includeHidden || !Path.GetFileName(entry).StartsWith(".", StringComparison.Ordinal))
        .Select(entry => ToWorkspaceFileEntry(workspace.RootPath, entry))
        .OrderBy(entry => entry.EntryType == "directory" ? 0 : 1)
        .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(new WorkspaceDirectoryView(
        workspace.Id,
        currentPath,
        ParentWorkspacePath(currentPath),
        BuildBreadcrumbs(currentPath),
        entries));
});

app.MapGet("/workspaces/{workspaceId}/files/content", async (
    string workspaceId,
    string path,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
    var targetPath = TryResolveWorkspacePath(workspace.RootPath, path);
    if (targetPath is null)
    {
        return Results.BadRequest(new { message = "Invalid workspace path." });
    }

    if (!File.Exists(targetPath))
    {
        return Results.NotFound(new { message = "File not found." });
    }

    var info = new FileInfo(targetPath);
    var relativePath = ToWorkspaceRelativePath(workspace.RootPath, targetPath);
    var previewSupported = IsPreviewSupported(targetPath);

    string? content = null;
    var truncated = false;
    if (previewSupported)
    {
        (content, truncated) = await ReadPreviewAsync(targetPath, cancellationToken);
    }

    return Results.Ok(new WorkspaceFilePreview(
        workspace.Id,
        relativePath,
        info.Name,
        info.Length,
        info.LastWriteTimeUtc,
        previewSupported,
        truncated,
        content));
});

app.MapGet("/workspaces/{workspaceId}/assets/{**assetPath}", async (
    string workspaceId,
    string assetPath,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
    var resolvedPath = TryResolveWorkspacePath(workspace.RootPath, assetPath);
    if (resolvedPath is null)
    {
        return Results.BadRequest(new { message = "Invalid asset path." });
    }

    return File.Exists(resolvedPath)
        ? Results.File(resolvedPath, GetContentType(resolvedPath))
        : Results.NotFound(new { message = "Asset not found.", requestedPath = assetPath });
});

app.MapGet("/workspaces/{workspaceId}/graphify", async (
    string workspaceId,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
    var diagnostics = await BuildGraphifyDiagnosticsAsync(workspace, cancellationToken);
    if (!string.IsNullOrWhiteSpace(diagnostics.PreferredAssetPath))
    {
        return Results.Redirect($"/workspaces/{workspaceId}/assets/{diagnostics.PreferredAssetPath}");
    }

    return Results.NotFound(new
    {
        message = "Nenhum HTML do Graphify foi encontrado para este workspace.",
        expectedPath = $"workspaces/{workspaceId}/graphs/graphify-out/graph.html"
    });
});

app.MapGet("/workspaces/{workspaceId}/graphify/sources", async (
    string workspaceId,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);
    var diagnostics = await BuildGraphifyDiagnosticsAsync(workspace, cancellationToken);
    return Results.Ok(diagnostics);
});

app.MapGet("/workspaces/{workspaceId}/graphify/{**assetPath}", (
    string workspaceId,
    string? assetPath,
    IConfiguration configuration) =>
{
    var root = configuration[$"{WorkspaceOptions.SectionName}:RootPath"]
        ?? "/app/workspaces";
    var graphRoot = Path.Combine(root, workspaceId, "graphs");
    var resolvedPath = TryResolveWorkspacePath(graphRoot, string.IsNullOrWhiteSpace(assetPath) ? "graphify-out/graph.html" : assetPath);

    if (resolvedPath is null)
    {
        return Results.BadRequest(new { message = "Invalid graph asset path." });
    }

    IResult result = File.Exists(resolvedPath)
        ? Results.File(resolvedPath, GetContentType(resolvedPath))
        : Results.NotFound(new
        {
            message = "Graphify HTML ainda não foi gerado para este workspace.",
            expectedPath = $"workspaces/{workspaceId}/graphs/graphify-out/graph.html"
        });

    return result;
});

app.MapGet("/ui", (IWebHostEnvironment environment) =>
    Results.File(
        Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "ui", "index.html"),
        "text/html"));

app.MapGet("/v1/models", async (
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var workspaces = await workspaceStore.ListWorkspacesAsync(cancellationToken);
    var models = new List<OpenAiModel>
    {
        new("knowledge-server", "model", "ai-knowledge-server", created)
    };

    models.AddRange(workspaces
        .OrderBy(workspace => workspace.Id, StringComparer.OrdinalIgnoreCase)
        .Select(workspace => new OpenAiModel(
            $"knowledge-server:{workspace.Id}",
            "model",
            "ai-knowledge-server",
            created)));

    return Results.Ok(new OpenAiModelsResponse(models));
});

app.MapPost("/v1/chat/completions", async (
    OpenAiChatCompletionRequest request,
    KnowledgeChatService chatService,
    CancellationToken cancellationToken) =>
{
    if (request.Messages.Count == 0)
    {
        return Results.BadRequest(new OpenAiErrorResponse(new OpenAiError(
            "messages is required.",
            "invalid_request_error",
            "messages")));
    }

    var workspaceId = OpenAiGateway.ResolveWorkspaceId(request);
    var prompt = OpenAiGateway.BuildPrompt(request.Messages);
    var maxResults = request.MaxResults is > 0 ? request.MaxResults.Value : 8;

    var response = await chatService.AskAsync(
        workspaceId,
        new ChatRequest(prompt, maxResults),
        cancellationToken);

    var content = OpenAiGateway.FormatAnswer(response);
    var completion = OpenAiChatCompletionResponse.Create(
        request.Model,
        content,
        prompt);

    return Results.Ok(completion);
});

app.MapGet("/mcp", () => Results.Ok(new
{
    message = "MCP endpoint is available. Use POST /mcp with JSON-RPC 2.0.",
    examples = new
    {
        initialize = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { }
        },
        listTools = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { }
        }
    }
}));

app.MapPost("/mcp", async (
    JsonRpcRequest request,
    KnowledgeChatService chatService,
    IWorkspaceStore workspaceStore,
    CancellationToken cancellationToken) =>
{
    var response = await McpGateway.HandleAsync(request, chatService, workspaceStore, cancellationToken);
    return Results.Json(response, McpGateway.JsonOptions);
});

app.MapGet("/mcp/info", () => Results.Ok(new
{
    name = "ai-knowledge-server",
    transports = new[] { "http-json-rpc" },
    endpoint = "/mcp",
    tools = McpGateway.ToolNames
}));

app.Run();

static string? TryResolveWorkspacePath(string workspaceRoot, string? relativePath)
{
    var normalized = string.IsNullOrWhiteSpace(relativePath)
        ? "."
        : relativePath.Replace('\\', '/').Trim();

    if (normalized is "." or "/")
    {
        return workspaceRoot;
    }

    var segments = normalized
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Where(segment => segment != "." && segment != "..");

    var candidate = Path.GetFullPath(Path.Combine(workspaceRoot, Path.Combine(segments.ToArray())));
    return candidate.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
        ? candidate
        : null;
}

static string ToWorkspaceRelativePath(string workspaceRoot, string targetPath)
{
    var relative = Path.GetRelativePath(workspaceRoot, targetPath).Replace('\\', '/');
    return relative == "." ? string.Empty : relative;
}

static string? ParentWorkspacePath(string currentPath)
{
    if (string.IsNullOrWhiteSpace(currentPath))
    {
        return null;
    }

    var parent = Path.GetDirectoryName(currentPath.Replace('/', Path.DirectorySeparatorChar))
        ?.Replace('\\', '/');

    return string.IsNullOrWhiteSpace(parent) ? string.Empty : parent;
}

static WorkspaceFileEntry ToWorkspaceFileEntry(string workspaceRoot, string targetPath)
{
    var relativePath = ToWorkspaceRelativePath(workspaceRoot, targetPath);

    if (Directory.Exists(targetPath))
    {
        var directory = new DirectoryInfo(targetPath);
        return new WorkspaceFileEntry(
            relativePath,
            directory.Name,
            "directory",
            null,
            directory.LastWriteTimeUtc,
            false);
    }

    var file = new FileInfo(targetPath);
    return new WorkspaceFileEntry(
        relativePath,
        file.Name,
        "file",
        file.Length,
        file.LastWriteTimeUtc,
        IsPreviewSupported(targetPath));
}

static IReadOnlyList<WorkspaceBreadcrumb> BuildBreadcrumbs(string currentPath)
{
    var breadcrumbs = new List<WorkspaceBreadcrumb>
    {
        new("workspace", string.Empty)
    };

    if (string.IsNullOrWhiteSpace(currentPath))
    {
        return breadcrumbs;
    }

    var segments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var pathParts = new List<string>();
    foreach (var segment in segments)
    {
        pathParts.Add(segment);
        breadcrumbs.Add(new WorkspaceBreadcrumb(segment, string.Join('/', pathParts)));
    }

    return breadcrumbs;
}

static bool IsPreviewSupported(string path)
{
    var extension = Path.GetExtension(path);
    return FileSystemWorkspaceStore.SupportedTextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
        || extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".css", StringComparison.OrdinalIgnoreCase);
}

static async Task<(string? Content, bool Truncated)> ReadPreviewAsync(string path, CancellationToken cancellationToken)
{
    const int maxChars = 200_000;

    await using var stream = File.OpenRead(path);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    var buffer = new char[maxChars + 1];
    var read = await reader.ReadBlockAsync(buffer.AsMemory(0, maxChars + 1), cancellationToken);
    var truncated = read > maxChars;
    var length = Math.Min(read, maxChars);
    return (new string(buffer, 0, length), truncated);
}

static string GetContentType(string path)
{
    var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    return provider.TryGetContentType(path, out var contentType)
        ? contentType
        : "application/octet-stream";
}

static async Task<GraphifyDiagnosticsView> BuildGraphifyDiagnosticsAsync(
    Workspace workspace,
    CancellationToken cancellationToken)
{
    var graphRoot = WorkspaceLayout.GraphsRoot(workspace.RootPath);
    var repositoryRoot = WorkspaceLayout.RepositoriesRoot(workspace.RootPath);
    var graphifyOutputRoot = Path.Combine(graphRoot, "graphify-out");

    var candidates = new List<GraphifySourceView>();
    candidates.AddRange(DiscoverGraphifySources(workspace.RootPath, graphifyOutputRoot, "workspace-graphify-out", "Graphify-out normalizado em graphs"));

    if (Directory.Exists(repositoryRoot))
    {
        foreach (var file in Directory.EnumerateFiles(repositoryRoot, "graph.html", SearchOption.AllDirectories)
                     .Where(path => path.Contains($"{Path.DirectorySeparatorChar}graphify-out{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("/graphify-out/", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(ToGraphifySourceView(workspace.RootPath, file, "repository-graphify-out", "Graphify-out detectado no repositório"));
        }
    }

    var distinctCandidates = candidates
        .GroupBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();

    var manifestPath = Path.Combine(graphRoot, "manifest.json");
    var processLogPath = Path.Combine(graphRoot, "graphify-process.json");

    GraphifyManifestView? manifest = null;
    if (File.Exists(manifestPath))
    {
        manifest = await ReadGraphifyManifestAsync(workspace.RootPath, manifestPath, cancellationToken);
    }

    GraphifyProcessLogView? processLog = null;
    if (File.Exists(processLogPath))
    {
        processLog = await ReadGraphifyProcessLogAsync(processLogPath, cancellationToken);
    }

    var preferred = distinctCandidates
        .OrderByDescending(candidate => candidate.Kind == "workspace-graphify-out")
        .ThenByDescending(candidate => candidate.Kind == "repository-graphify-out")
        .ThenBy(candidate => candidate.RelativePath.EndsWith("graph.html", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    return new GraphifyDiagnosticsView(
        workspace.Id,
        preferred?.RelativePath,
        preferred is not null,
        distinctCandidates,
        manifest,
        processLog,
        preferred is null
            ? "O grafo ainda não foi gerado pelo Graphify."
            : preferred.Kind == "workspace-graphify-out"
                ? "Foi encontrada a UI original gerada pelo Graphify em graphs/graphify-out/graph.html."
                : preferred.Kind == "repository-graphify-out"
                    ? "Foi encontrada a UI original gerada pelo Graphify em inputs/repositories/.../graphify-out/graph.html."
                    : "Foi encontrada uma saída HTML do Graphify no workspace.");
}

static IReadOnlyCollection<GraphifySourceView> DiscoverGraphifySources(
    string workspaceRoot,
    string graphRoot,
    string kind,
    string label)
{
    var candidates = new List<GraphifySourceView>();
    foreach (var fileName in new[] { "graph.html", "index.html" })
    {
        var path = Path.Combine(graphRoot, fileName);
        if (File.Exists(path))
        {
            candidates.Add(ToGraphifySourceView(workspaceRoot, path, kind, label));
        }
    }

    return candidates;
}

static GraphifySourceView ToGraphifySourceView(string workspaceRoot, string absolutePath, string kind, string label)
{
    var relativePath = ToWorkspaceRelativePath(workspaceRoot, absolutePath);
    var info = new FileInfo(absolutePath);
    return new GraphifySourceView(
        relativePath,
        $"/workspaces/{{workspaceId}}/assets/{relativePath}",
        kind,
        label,
        info.LastWriteTimeUtc,
        info.Length);
}

static async Task<GraphifyManifestView?> ReadGraphifyManifestAsync(
    string workspaceRoot,
    string manifestPath,
    CancellationToken cancellationToken)
{
    try
    {
        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var artifacts = root.TryGetProperty("artifacts", out var artifactsElement) && artifactsElement.ValueKind == JsonValueKind.Array
            ? artifactsElement.EnumerateArray()
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => ToWorkspaceRelativePath(workspaceRoot, value!))
                .ToArray()
            : [];

        return new GraphifyManifestView(
            root.TryGetProperty("generator", out var generator) ? generator.GetString() : null,
            root.TryGetProperty("generatedAt", out var generatedAt) && generatedAt.TryGetDateTimeOffset(out var generatedAtValue) ? generatedAtValue : null,
            root.TryGetProperty("nodeCount", out var nodeCount) && nodeCount.TryGetInt32(out var nodeCountValue) ? nodeCountValue : null,
            root.TryGetProperty("edgeCount", out var edgeCount) && edgeCount.TryGetInt32(out var edgeCountValue) ? edgeCountValue : null,
            artifacts);
    }
    catch
    {
        return null;
    }
}

static async Task<GraphifyProcessLogView?> ReadGraphifyProcessLogAsync(
    string processLogPath,
    CancellationToken cancellationToken)
{
    try
    {
        await using var stream = File.OpenRead(processLogPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        static string? GetOptionalString(JsonElement rootElement, string propertyName)
            => rootElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement rootElement, string propertyName)
            => rootElement.TryGetProperty(propertyName, out var property) && property.TryGetDateTimeOffset(out var value)
                ? value
                : null;

        return new GraphifyProcessLogView(
            GetOptionalString(root, "command"),
            GetOptionalString(root, "arguments"),
            root.TryGetProperty("exitCode", out var exitCode) && exitCode.TryGetInt32(out var exitCodeValue) ? exitCodeValue : null,
            GetOptionalDateTimeOffset(root, "startedAt") ?? GetOptionalDateTimeOffset(root, "failedAt"),
            GetOptionalDateTimeOffset(root, "finishedAt"),
            GetOptionalString(root, "error"),
            TrimLogPreview(GetOptionalString(root, "stdout")),
            TrimLogPreview(GetOptionalString(root, "stderr")));
    }
    catch
    {
        return null;
    }
}

static string? TrimLogPreview(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    const int maxLength = 4000;
    return value.Length <= maxLength
        ? value
        : $"{value[..maxLength]}\n\n[truncado]";
}

internal sealed record RegisterRepositoryRequest(
    string Name,
    string? RelativePath = null,
    string? RemoteUrl = null,
    string? Branch = null);

internal sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string? JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

internal sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result")] object? Result = null,
    [property: JsonPropertyName("error")] JsonRpcError? Error = null);

internal sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] object? Data = null);

internal sealed record WorkspaceDirectoryView(
    string WorkspaceId,
    string CurrentPath,
    string? ParentPath,
    IReadOnlyList<WorkspaceBreadcrumb> Breadcrumbs,
    IReadOnlyList<WorkspaceFileEntry> Entries);

internal sealed record WorkspaceBreadcrumb(string Name, string RelativePath);

internal sealed record WorkspaceFileEntry(
    string RelativePath,
    string Name,
    string EntryType,
    long? SizeBytes,
    DateTimeOffset LastModifiedAt,
    bool PreviewSupported);

internal sealed record WorkspaceFilePreview(
    string WorkspaceId,
    string RelativePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset LastModifiedAt,
    bool PreviewSupported,
    bool Truncated,
    string? Content);

internal sealed record GraphifyDiagnosticsView(
    string WorkspaceId,
    string? PreferredAssetPath,
    bool HasVisualization,
    IReadOnlyCollection<GraphifySourceView> Sources,
    GraphifyManifestView? Manifest,
    GraphifyProcessLogView? ProcessLog,
    string StatusMessage);

internal sealed record GraphifySourceView(
    string RelativePath,
    string AssetRouteTemplate,
    string Kind,
    string Label,
    DateTimeOffset LastModifiedAt,
    long SizeBytes);

internal sealed record GraphifyManifestView(
    string? Generator,
    DateTimeOffset? GeneratedAt,
    int? NodeCount,
    int? EdgeCount,
    IReadOnlyCollection<string> Artifacts);

internal sealed record GraphifyProcessLogView(
    string? Command,
    string? Arguments,
    int? ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    string? Stdout,
    string? Stderr);

internal sealed record OpenAiModelsResponse(
    [property: JsonPropertyName("data")] IReadOnlyCollection<OpenAiModel> Data,
    [property: JsonPropertyName("object")] string Object = "list");

internal sealed record OpenAiModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("owned_by")] string OwnedBy,
    [property: JsonPropertyName("created")] long Created);

internal sealed record OpenAiChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyCollection<OpenAiChatMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream = false,
    [property: JsonPropertyName("workspaceId")] string? WorkspaceId = null,
    [property: JsonPropertyName("maxResults")] int? MaxResults = null);

internal sealed record OpenAiChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] JsonElement Content);

internal sealed record OpenAiChatCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyCollection<OpenAiChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage Usage)
{
    public static OpenAiChatCompletionResponse Create(string model, string answer, string prompt)
    {
        return new OpenAiChatCompletionResponse(
            $"chatcmpl-{Guid.NewGuid():N}",
            "chat.completion",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            string.IsNullOrWhiteSpace(model) ? "knowledge-server" : model,
            [
                new OpenAiChoice(
                    0,
                    new OpenAiAssistantMessage("assistant", answer),
                    "stop")

            ],
            new OpenAiUsage(
                EstimateTokens(prompt),
                EstimateTokens(answer),
                EstimateTokens(prompt) + EstimateTokens(answer)));
    }

    private static int EstimateTokens(string value)
    {
        return Math.Max(1, value.Length / 4);
    }
}

internal sealed record OpenAiChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] OpenAiAssistantMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason);

internal sealed record OpenAiAssistantMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OpenAiUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

internal sealed record OpenAiErrorResponse(
    [property: JsonPropertyName("error")] OpenAiError Error);

internal sealed record OpenAiError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("param")] string? Param = null,
    [property: JsonPropertyName("code")] string? Code = null);

internal static class OpenAiGateway
{
    public static string ResolveWorkspaceId(OpenAiChatCompletionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            return request.WorkspaceId;
        }

        const string prefix = "knowledge-server:";
        if (!string.IsNullOrWhiteSpace(request.Model)
            && request.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return request.Model[prefix.Length..];
        }

        return "default";
    }

    public static string BuildPrompt(IReadOnlyCollection<OpenAiChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            var content = ReadContent(message.Content);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            builder.AppendLine($"{message.Role}: {content}");
        }

        return builder.ToString().Trim();
    }

    public static string FormatAnswer(ChatResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine(response.Answer.Trim());

        if (response.References.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Referências:");
            foreach (var reference in response.References)
            {
                builder.AppendLine($"- {reference.RelativePath}");
            }
        }

        return builder.ToString().Trim();
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.ToString();
        }

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                parts.Add(item.GetString() ?? "");
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? "");
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}

internal static class McpGateway
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly string[] ToolNames =
    [
        "search_business_rules",
        "find_related_code",
        "get_service_summary",
        "explain_flow",
        "find_references",
        "find_impacted_services",
        "compare_business_rule_with_code",
        "find_divergences"
    ];

    public static async Task<JsonRpcResponse> HandleAsync(
        JsonRpcRequest request,
        KnowledgeChatService chatService,
        IWorkspaceStore workspaceStore,
        CancellationToken cancellationToken)
    {
        if (request.JsonRpc is not null && request.JsonRpc != "2.0")
        {
            return Error(request.Id, -32600, "Invalid JSON-RPC version. Expected 2.0.");
        }

        return request.Method switch
        {
            "initialize" => Success(request.Id, Initialize()),
            "tools/list" => Success(request.Id, new { tools = Tools() }),
            "tools/call" => Success(
                request.Id,
                await CallToolAsync(request.Params, chatService, workspaceStore, cancellationToken)),
            null or "" => Error(request.Id, -32600, "JSON-RPC method is required."),
            _ => Error(request.Id, -32601, $"Method '{request.Method}' is not supported.")
        };
    }

    private static object Initialize() => new
    {
        protocolVersion = "2024-11-05",
        capabilities = new
        {
            tools = new { }
        },
        serverInfo = new
        {
            name = "ai-knowledge-server",
            version = "0.1.0"
        }
    };

    private static IReadOnlyCollection<object> Tools() =>
    [
        Tool(
            "search_business_rules",
            "Searches local workspace documents for business rules and policy-like references.",
            ["workspaceId", "query", "maxResults"]),
        Tool(
            "find_related_code",
            "Finds candidate code files related to a feature, rule, service, class, or symbol.",
            ["workspaceId", "query", "maxResults"]),
        Tool(
            "get_service_summary",
            "Summarizes local references for a service, module, endpoint, or component.",
            ["workspaceId", "service", "maxResults"]),
        Tool(
            "explain_flow",
            "Explains a probable flow from matching workspace references.",
            ["workspaceId", "flow", "maxResults"]),
        Tool(
            "find_references",
            "Finds textual references to a symbol, endpoint, rule, file, or concept.",
            ["workspaceId", "query", "maxResults"]),
        Tool(
            "find_impacted_services",
            "Finds likely impacted services or modules for a proposed change.",
            ["workspaceId", "query", "maxResults"]),
        Tool(
            "compare_business_rule_with_code",
            "Compares a business rule description with code references found in the workspace.",
            ["workspaceId", "businessRule", "codeQuery", "maxResults"]),
        Tool(
            "find_divergences",
            "Searches for likely inconsistencies between rules, docs, summaries, and code.",
            ["workspaceId", "query", "maxResults"])
    ];

    private static async Task<object> CallToolAsync(
        JsonElement? parameters,
        KnowledgeChatService chatService,
        IWorkspaceStore workspaceStore,
        CancellationToken cancellationToken)
    {
        var toolName = GetString(parameters, "name");
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return ToolError("Tool name is required in params.name.");
        }

        if (!ToolNames.Contains(toolName, StringComparer.Ordinal))
        {
            return ToolError($"Unknown tool '{toolName}'.");
        }

        var arguments = GetObject(parameters, "arguments");
        var workspaceId = GetString(arguments, "workspaceId") ?? "default";
        var maxResults = GetInt(arguments, "maxResults", 8);

        try
        {
            var result = toolName switch
            {
                "search_business_rules" => await SearchAsync(
                    workspaceStore,
                    workspaceId,
                    Query(arguments),
                    maxResults,
                    "Business rule search",
                    "Search terms should describe a rule, policy, invariant, validation, or decision.",
                    cancellationToken),
                "find_related_code" => await SearchAsync(
                    workspaceStore,
                    workspaceId,
                    Query(arguments),
                    maxResults,
                    "Related code search",
                    "Search terms should identify a feature, service, class, method, endpoint, or technical concept.",
                    cancellationToken),
                "get_service_summary" => await SummarizeAsync(
                    chatService,
                    workspaceStore,
                    workspaceId,
                    GetString(arguments, "service") ?? Query(arguments),
                    maxResults,
                    "Service summary",
                    cancellationToken),
                "explain_flow" => await SummarizeAsync(
                    chatService,
                    workspaceStore,
                    workspaceId,
                    GetString(arguments, "flow") ?? Query(arguments),
                    maxResults,
                    "Flow explanation",
                    cancellationToken),
                "find_references" => await SearchAsync(
                    workspaceStore,
                    workspaceId,
                    Query(arguments),
                    maxResults,
                    "Reference search",
                    "Search terms should identify the symbol, endpoint, file, rule, or concept.",
                    cancellationToken),
                "find_impacted_services" => await SearchAsync(
                    workspaceStore,
                    workspaceId,
                    ImpactQuery(arguments),
                    maxResults,
                    "Impacted services",
                    "Search terms should describe the change, dependency, integration, or business capability.",
                    cancellationToken),
                "compare_business_rule_with_code" => await CompareAsync(
                    workspaceStore,
                    workspaceId,
                    GetString(arguments, "businessRule") ?? Query(arguments),
                    GetString(arguments, "codeQuery") ?? Query(arguments),
                    maxResults,
                    cancellationToken),
                "find_divergences" => await SearchAsync(
                    workspaceStore,
                    workspaceId,
                    DivergenceQuery(arguments),
                    maxResults,
                    "Divergence search",
                    "Search terms should describe the expected rule, implementation, behavior, or mismatch.",
                    cancellationToken),
                _ => ToolText("Unsupported tool.")
            };

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ToolText($"The tool could not read all workspace data: {exception.Message}");
        }
    }

    private static async Task<object> SearchAsync(
        IWorkspaceStore workspaceStore,
        string workspaceId,
        string query,
        int maxResults,
        string title,
        string hint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolText($"{title}: query is required. {hint}");
        }

        var searchResults = await workspaceStore.SearchAsync(workspaceId, query, maxResults, cancellationToken);
        var documents = await workspaceStore.ListDocumentsAsync(workspaceId, cancellationToken);
        var jobs = await workspaceStore.ListIndexingJobsAsync(workspaceId, cancellationToken);

        var text = BuildSearchText(title, workspaceId, query, searchResults, documents.Count, jobs);
        return ToolText(text, new
        {
            workspaceId,
            query,
            results = searchResults,
            documents = documents.Count,
            jobs = jobs.Take(5)
        });
    }

    private static async Task<object> SummarizeAsync(
        KnowledgeChatService chatService,
        IWorkspaceStore workspaceStore,
        string workspaceId,
        string query,
        int maxResults,
        string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolText($"{title}: service, flow, or query is required.");
        }

        var response = await chatService.AskAsync(
            workspaceId,
            new ChatRequest($"Summarize and explain local knowledge about: {query}", maxResults),
            cancellationToken);

        var documents = await workspaceStore.ListDocumentsAsync(workspaceId, cancellationToken);
        var text = new StringBuilder()
            .AppendLine($"{title} for workspace '{workspaceId}'")
            .AppendLine($"Query: {query}")
            .AppendLine()
            .AppendLine(response.Answer)
            .AppendLine()
            .Append(RenderReferences(response.References, documents.Count))
            .ToString();

        return ToolText(text, new
        {
            workspaceId,
            query,
            answer = response.Answer,
            references = response.References,
            documents = documents.Count
        });
    }

    private static async Task<object> CompareAsync(
        IWorkspaceStore workspaceStore,
        string workspaceId,
        string businessRule,
        string codeQuery,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(businessRule) && string.IsNullOrWhiteSpace(codeQuery))
        {
            return ToolText("Comparison requires businessRule, codeQuery, or query.");
        }

        var ruleQuery = string.IsNullOrWhiteSpace(businessRule) ? codeQuery : businessRule;
        var implementationQuery = string.IsNullOrWhiteSpace(codeQuery) ? businessRule : codeQuery;
        var ruleResults = await workspaceStore.SearchAsync(workspaceId, ruleQuery, maxResults, cancellationToken);
        var codeResults = await workspaceStore.SearchAsync(workspaceId, implementationQuery, maxResults, cancellationToken);
        var documents = await workspaceStore.ListDocumentsAsync(workspaceId, cancellationToken);

        var text = new StringBuilder()
            .AppendLine($"Business rule/code comparison for workspace '{workspaceId}'")
            .AppendLine($"Business rule query: {ruleQuery}")
            .AppendLine($"Code query: {implementationQuery}")
            .AppendLine()
            .AppendLine("Rule-side references:")
            .AppendLine(RenderReferences(ruleResults, documents.Count))
            .AppendLine()
            .AppendLine("Code-side references:")
            .AppendLine(RenderReferences(codeResults, documents.Count))
            .AppendLine()
            .AppendLine("Interpretation: treat these as candidates. A human or LLM should compare the returned snippets for missing validation, conflicting wording, or behavior implemented only on one side.")
            .ToString();

        return ToolText(text, new
        {
            workspaceId,
            businessRule = ruleQuery,
            codeQuery = implementationQuery,
            ruleReferences = ruleResults,
            codeReferences = codeResults,
            documents = documents.Count
        });
    }

    private static string BuildSearchText(
        string title,
        string workspaceId,
        string query,
        IReadOnlyCollection<SearchResult> results,
        int documentCount,
        IReadOnlyCollection<IndexingJob> jobs)
    {
        var text = new StringBuilder()
            .AppendLine($"{title} for workspace '{workspaceId}'")
            .AppendLine($"Query: {query}")
            .AppendLine()
            .Append(RenderReferences(results, documentCount));

        if (results.Count == 0 && jobs.Count > 0)
        {
            text.AppendLine()
                .AppendLine("Recent indexing jobs:")
                .AppendJoin(
                    Environment.NewLine,
                    jobs.Take(5).Select(job => $"- {job.Status}: {job.Reason} ({job.SourcePath}) at {job.CreatedAt:u}"));
        }

        return text.ToString();
    }

    private static string RenderReferences(IReadOnlyCollection<SearchResult> references, int documentCount)
    {
        if (references.Count == 0)
        {
            return documentCount == 0
                ? "No references found. The workspace exists but has no listed documents yet; upload documents or repository exports and reindex before asking deeper questions."
                : $"No matching references found across {documentCount} listed document(s). Try broader terms or confirm that generated graph/summaries/code indexes exist in the workspace.";
        }

        var text = new StringBuilder();
        foreach (var reference in references)
        {
            text.AppendLine($"- {reference.RelativePath} (score {reference.Score})")
                .AppendLine($"  {reference.Snippet}");
        }

        return text.ToString();
    }

    private static object Tool(string name, string description, string[] properties) => new
    {
        name,
        description,
        inputSchema = new
        {
            type = "object",
            properties = properties.ToDictionary(
                property => property,
                property => ToolProperty(property)),
            required = properties.Contains("workspaceId") ? new[] { "workspaceId" } : []
        }
    };

    private static object ToolProperty(string property) =>
        property is "maxResults"
            ? new { type = "integer", description = "Maximum number of references to return." }
            : new { type = "string", description = $"{property} argument." };

    private static JsonRpcResponse Success(JsonElement? id, object result) => new("2.0", id, result);

    private static JsonRpcResponse Error(JsonElement? id, int code, string message, object? data = null) =>
        new("2.0", id, Error: new JsonRpcError(code, message, data));

    private static object ToolText(string text, object? structuredContent = null) => new
    {
        content = new[]
        {
            new
            {
                type = "text",
                text
            }
        },
        structuredContent
    };

    private static object ToolError(string text) => new
    {
        isError = true,
        content = new[]
        {
            new
            {
                type = "text",
                text
            }
        }
    };

    private static string Query(JsonElement? arguments) =>
        GetString(arguments, "query")
        ?? GetString(arguments, "message")
        ?? GetString(arguments, "symbol")
        ?? GetString(arguments, "path")
        ?? "";

    private static string ImpactQuery(JsonElement? arguments)
    {
        var query = Query(arguments);
        var change = GetString(arguments, "change");
        return string.Join(' ', new[] { query, change }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string DivergenceQuery(JsonElement? arguments)
    {
        var query = Query(arguments);
        var expected = GetString(arguments, "expected");
        var actual = GetString(arguments, "actual");
        return string.Join(' ', new[] { query, expected, actual }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static JsonElement? GetObject(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object
            ? property
            : null;
    }

    private static string? GetString(JsonElement? element, string name, string? fallback = null)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return fallback;
        }

        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : fallback;
    }

    private static int GetInt(JsonElement? element, string name, int fallback)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return fallback;
        }

        return value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result)
            ? Math.Clamp(result, 1, 50)
            : fallback;
    }
}
