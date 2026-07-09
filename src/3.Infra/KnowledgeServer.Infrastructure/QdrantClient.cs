using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeServer.Application;
using KnowledgeServer.Domain;
using Microsoft.Extensions.Options;

namespace KnowledgeServer.Infrastructure;

public sealed class QdrantClient(
    HttpClient httpClient,
    IOptions<QdrantOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Uri endpoint = new(options.Value.Endpoint.TrimEnd('/') + "/");

    public async Task EnsureCollectionAsync(
        string workspaceId,
        int vectorSize,
        CancellationToken cancellationToken)
    {
        var collection = CollectionName(workspaceId);
        using var getResponse = await httpClient.GetAsync(
            new Uri(endpoint, $"collections/{collection}"),
            cancellationToken);

        if (getResponse.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        var request = new
        {
            vectors = new
            {
                size = vectorSize,
                distance = "Cosine"
            }
        };

        using var putResponse = await httpClient.PutAsJsonAsync(
            new Uri(endpoint, $"collections/{collection}"),
            request,
            JsonOptions,
            cancellationToken);

        putResponse.EnsureSuccessStatusCode();
    }

    public async Task UpsertAsync(
        string workspaceId,
        IReadOnlyCollection<EmbeddedChunk> chunks,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        var points = chunks.Select(chunk => new
        {
            id = chunk.Chunk.Id,
            vector = chunk.Vector,
            payload = new
            {
                workspaceId = chunk.Chunk.WorkspaceId,
                relativePath = chunk.Chunk.RelativePath,
                sourceKind = chunk.Chunk.SourceKind,
                content = chunk.Chunk.Content,
                startLine = chunk.Chunk.StartLine,
                endLine = chunk.Chunk.EndLine
            }
        });

        var request = new { points };
        using var response = await httpClient.PutAsJsonAsync(
            new Uri(endpoint, $"collections/{CollectionName(workspaceId)}/points?wait=true"),
            request,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyCollection<SearchResult>> SearchAsync(
        string workspaceId,
        float[] vector,
        int limit,
        CancellationToken cancellationToken)
    {
        if (vector.Length == 0)
        {
            return [];
        }

        var request = new
        {
            vector,
            limit,
            with_payload = true
        };

        using var response = await httpClient.PostAsJsonAsync(
            new Uri(endpoint, $"collections/{CollectionName(workspaceId)}/points/search"),
            request,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<QdrantSearchResponse>(
            JsonOptions,
            cancellationToken);

        return payload?.Result?
            .Where(point => point.Payload is not null)
            .Select(point => new SearchResult(
                point.Payload!.WorkspaceId ?? workspaceId,
                point.Payload.RelativePath ?? "unknown",
                point.Payload.Content ?? string.Empty,
                (int)Math.Round(point.Score * 100)))
            .ToArray() ?? [];
    }

    public static string CollectionName(string workspaceId)
    {
        var normalized = new string(workspaceId
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());

        return $"workspace_{normalized}";
    }

    public sealed record EmbeddedChunk(KnowledgeChunk Chunk, float[] Vector);

    private sealed record QdrantSearchResponse(
        [property: JsonPropertyName("result")] QdrantPoint[] Result);

    private sealed record QdrantPoint(
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("payload")] QdrantPayload? Payload);

    private sealed record QdrantPayload(
        [property: JsonPropertyName("workspaceId")] string? WorkspaceId,
        [property: JsonPropertyName("relativePath")] string? RelativePath,
        [property: JsonPropertyName("content")] string? Content);
}

