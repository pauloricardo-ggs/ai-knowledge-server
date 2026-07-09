using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeServer.Application;
using Microsoft.Extensions.Options;

namespace KnowledgeServer.Infrastructure;

public sealed class OllamaClient(
    HttpClient httpClient,
    IOptions<OllamaOptions> ollamaOptions,
    IOptions<ModelOptions> modelOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Uri endpoint = new(ollamaOptions.Value.Endpoint.TrimEnd('/') + "/");
    private readonly ModelOptions models = modelOptions.Value;

    public async Task EnsureModelsAsync(CancellationToken cancellationToken)
    {
        if (!models.AutoPull)
        {
            return;
        }

        await PullModelAsync(models.EmbeddingModel, cancellationToken);
        await PullModelAsync(models.ChatModel, cancellationToken);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = models.EmbeddingModel,
            input = text
        };

        using var response = await httpClient.PostAsJsonAsync(
            new Uri(endpoint, "api/embed"),
            request,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(
            JsonOptions,
            cancellationToken);

        var embedding = payload?.Embeddings?.FirstOrDefault();
        return embedding is null ? [] : embedding.Select(value => (float)value).ToArray();
    }

    public async Task<string> GenerateAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            model = models.ChatModel,
            prompt,
            stream = false
        };

        using var response = await httpClient.PostAsJsonAsync(
            new Uri(endpoint, "api/generate"),
            request,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GenerateResponse>(
            JsonOptions,
            cancellationToken);

        return string.IsNullOrWhiteSpace(payload?.Response)
            ? "O modelo local não retornou uma resposta."
            : payload.Response.Trim();
    }

    private async Task PullModelAsync(string model, CancellationToken cancellationToken)
    {
        var request = new
        {
            name = model,
            stream = false
        };

        using var response = await httpClient.PostAsJsonAsync(
            new Uri(endpoint, "api/pull"),
            request,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] double[][] Embeddings);

    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string Response);
}

