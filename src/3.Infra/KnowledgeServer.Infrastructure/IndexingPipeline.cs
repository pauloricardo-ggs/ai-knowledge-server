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

    private static readonly string[] IndexedRoots = ["documents", "repositories", "summaries", "roslyn"];

    public async Task IndexWorkspaceAsync(
        string workspaceId,
        string reason,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceStore.GetOrCreateWorkspaceAsync(workspaceId, cancellationToken);

        await codeIntelligenceService.GenerateAsync(
            new CodeIntelligenceRequest(workspace.Id),
            cancellationToken);

        await graphifyService.GenerateAsync(
            new GraphifyRequest(workspace.Id),
            cancellationToken);

        var chunks = await BuildChunksAsync(workspace, cancellationToken);

        var chunksPath = Path.Combine(workspace.RootPath, "embeddings", "chunks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(chunksPath)!);
        await File.WriteAllTextAsync(
            chunksPath,
            JsonSerializer.Serialize(chunks, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        if (chunks.Count == 0)
        {
            logger.LogInformation("Workspace {WorkspaceId} has no chunks to index", workspace.Id);
            return;
        }

        await ollamaClient.EnsureModelsAsync(cancellationToken);

        var embedded = new List<QdrantClient.EmbeddedChunk>();
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return;
        }

        await qdrantClient.EnsureCollectionAsync(
            workspace.Id,
            embedded[0].Vector.Length,
            cancellationToken);

        await qdrantClient.UpsertAsync(workspace.Id, embedded, cancellationToken);

        var summaryPath = Path.Combine(workspace.RootPath, "summaries", "indexing-summary.json");
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
    }

    private static async Task<IReadOnlyCollection<KnowledgeChunk>> BuildChunksAsync(
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        var chunks = new List<KnowledgeChunk>();

        foreach (var rootName in IndexedRoots)
        {
            var root = Path.Combine(workspace.RootPath, rootName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsIgnored(file) || !IsSupported(file))
                {
                    continue;
                }

                var content = await TryReadAsync(file, cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                chunks.AddRange(SplitFile(workspace, file, content));
            }
        }

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
            "documents" => "document",
            "repositories" => "code",
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
