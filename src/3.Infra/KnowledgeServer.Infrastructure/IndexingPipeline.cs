using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeServer.Application;
using KnowledgeServer.Domain;
using Microsoft.Extensions.Logging;

namespace KnowledgeServer.Infrastructure;

public sealed class IndexingPipeline(
    IWorkspaceStore workspaceStore,
    ICodeIntelligenceService codeIntelligenceService,
    IGraphifyService graphifyService,
    OllamaClient ollamaClient,
    QdrantClient qdrantClient,
    ILogger<IndexingPipeline> logger) : IIndexingPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] IndexedRoots =
    [
        $"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.DocumentsRootName}",
        $"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.RepositoriesRootName}",
        "summaries",
        "roslyn"
    ];

    public async Task IndexWorkspaceAsync(
        string workspaceId,
        string jobId,
        string reason,
        Func<IndexingProgress, Task> reportProgressAsync,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);

        await reportProgressAsync(new IndexingProgress(
            "preparing",
            "Preparando pipeline de indexação.",
            workspace.RootPath));

        await reportProgressAsync(new IndexingProgress(
            "code-intelligence",
            "Gerando artefatos determinísticos de código.",
            $"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.RepositoriesRootName}/"));
        await codeIntelligenceService.GenerateAsync(
            new CodeIntelligenceRequest(workspace.Id),
            cancellationToken);

        await reportProgressAsync(new IndexingProgress(
            "graphify",
            "Gerando grafo do workspace.",
            "graphs/"));
        await graphifyService.GenerateAsync(
            new GraphifyRequest(workspace.Id),
            cancellationToken);

        var chunks = await BuildChunksAsync(workspace, reportProgressAsync, cancellationToken);

        var chunksPath = Path.Combine(workspace.RootPath, "embeddings", "chunks.json");
        chunksPath = Path.Combine(WorkspaceLayout.EmbeddingsRoot(workspace.RootPath), "chunks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(chunksPath)!);
        await File.WriteAllTextAsync(
            chunksPath,
            JsonSerializer.Serialize(chunks, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        if (chunks.Count == 0)
        {
            logger.LogInformation("Workspace {WorkspaceId} has no chunks to index", workspace.Id);
            await reportProgressAsync(new IndexingProgress(
                "chunking",
                "Nenhum chunk foi gerado para indexação.",
                null,
                0,
                0,
                []));
            return;
        }

        await reportProgressAsync(new IndexingProgress(
            "embeddings",
            "Garantindo modelos de embedding.",
            "embeddings/"));
        await ollamaClient.EnsureModelsAsync(cancellationToken);

        var embedded = new List<QdrantClient.EmbeddedChunk>();
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = chunks.ElementAt(index);
            var pendingPaths = chunks
                .Skip(index + 1)
                .Select(item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            await reportProgressAsync(new IndexingProgress(
                "embeddings",
                "Gerando embedding para chunk.",
                chunk.RelativePath,
                chunks.Count,
                index,
                pendingPaths));

            var vector = await ollamaClient.EmbedAsync(chunk.Content, cancellationToken);
            if (vector.Length == 0)
            {
                continue;
            }

            embedded.Add(new QdrantClient.EmbeddedChunk(chunk, vector));
        }

        if (embedded.Count == 0)
        {
            logger.LogWarning("No embeddings generated for workspace {WorkspaceId}", workspace.Id);
            await reportProgressAsync(new IndexingProgress(
                "embeddings",
                "Nenhum embedding foi gerado.",
                null,
                chunks.Count,
                embedded.Count,
                []));
            return;
        }

        await reportProgressAsync(new IndexingProgress(
            "qdrant",
            "Garantindo collection do Qdrant.",
            QdrantClient.CollectionName(workspace.Id),
            embedded.Count,
            embedded.Count,
            []));
        await qdrantClient.EnsureCollectionAsync(
            workspace.Id,
            embedded[0].Vector.Length,
            cancellationToken);

        await reportProgressAsync(new IndexingProgress(
            "qdrant",
            "Persistindo vetores no Qdrant.",
            QdrantClient.CollectionName(workspace.Id),
            embedded.Count,
            embedded.Count,
            []));
        await qdrantClient.UpsertAsync(workspace.Id, embedded, cancellationToken);

        var summaryPath = Path.Combine(WorkspaceLayout.SummariesRoot(workspace.RootPath), "indexing-summary.json");
        await File.WriteAllTextAsync(
            summaryPath,
            JsonSerializer.Serialize(new
            {
                workspaceId = workspace.Id,
                reason,
                chunkCount = chunks.Count,
                embeddedCount = embedded.Count,
                indexedAt = DateTimeOffset.UtcNow,
                qdrantCollection = QdrantClient.CollectionName(workspace.Id)
            }, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        await reportProgressAsync(new IndexingProgress(
            "summary",
            "Resumo final de indexação gravado.",
            Path.GetRelativePath(workspace.RootPath, summaryPath),
            embedded.Count,
            embedded.Count,
            []));
    }

    private static async Task<IReadOnlyCollection<KnowledgeChunk>> BuildChunksAsync(
        Workspace workspace,
        Func<IndexingProgress, Task> reportProgressAsync,
        CancellationToken cancellationToken)
    {
        var chunks = new List<KnowledgeChunk>();
        var files = IndexedRoots
            .Select(rootName => Path.Combine(workspace.RootPath, rootName))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(file => !IsIgnored(file) && IsSupported(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];
            var relativePath = Path.GetRelativePath(workspace.RootPath, file);
            var pendingPaths = files
                .Skip(index + 1)
                .Select(path => Path.GetRelativePath(workspace.RootPath, path))
                .Take(8)
                .ToArray();

            await reportProgressAsync(new IndexingProgress(
                "chunking",
                "Lendo arquivos para gerar chunks.",
                relativePath,
                files.Length,
                index,
                pendingPaths));

            var content = await TryReadAsync(file, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            chunks.AddRange(SplitFile(workspace, file, content));
        }

        await reportProgressAsync(new IndexingProgress(
            "chunking",
            "Geração de chunks concluída.",
            null,
            files.Length,
            files.Length,
            []));

        return chunks;
    }

    private static IEnumerable<KnowledgeChunk> SplitFile(Workspace workspace, string file, string content)
    {
        var relativePath = Path.GetRelativePath(workspace.RootPath, file);
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        const int maxLines = 80;
        const int overlap = 10;

        for (var start = 0; start < lines.Length; start += maxLines - overlap)
        {
            var selected = lines.Skip(start).Take(maxLines).ToArray();
            var chunkContent = string.Join('\n', selected).Trim();
            if (chunkContent.Length < 20)
            {
                continue;
            }

            var end = Math.Min(lines.Length, start + selected.Length);
            yield return new KnowledgeChunk(
                BuildChunkId(workspace.Id, relativePath, start + 1, end),
                workspace.Id,
                relativePath,
                SourceKind(relativePath),
                chunkContent,
                start + 1,
                end);

            if (end == lines.Length)
            {
                break;
            }
        }
    }

    private static string BuildChunkId(string workspaceId, string relativePath, int startLine, int endLine)
    {
        var input = $"{workspaceId}:{relativePath}:{startLine}:{endLine}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(bytes[..16]).ToString();
    }

    private static string SourceKind(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
        return first switch
        {
            WorkspaceLayout.InputsRootName when relativePath.StartsWith($"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.DocumentsRootName}/", StringComparison.OrdinalIgnoreCase) => "document",
            WorkspaceLayout.InputsRootName when relativePath.StartsWith($"{WorkspaceLayout.InputsRootName}/{WorkspaceLayout.RepositoriesRootName}/", StringComparison.OrdinalIgnoreCase) => "code",
            "roslyn" => "roslyn",
            "summaries" => "summary",
            _ => "unknown"
        };
    }

    private static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return FileSystemWorkspaceStore.SupportedTextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIgnored(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is ".git" or "bin" or "obj" or "node_modules" or "cache" or "logs");
    }

    private static async Task<string?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 2_000_000)
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
