using System.Text;
using KnowledgeServer.Application;
using KnowledgeServer.Domain;

namespace KnowledgeServer.Infrastructure;

public sealed class OllamaKnowledgeGenerator(
    IKnowledgeSearch knowledgeSearch,
    OllamaClient ollamaClient) : IKnowledgeGenerator
{
    public async Task<KnowledgeAnswer> AnswerAsync(
        string workspaceId,
        string question,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var references = await knowledgeSearch.SearchAsync(
            workspaceId,
            question,
            maxResults,
            cancellationToken);

        if (references.Count == 0)
        {
            return new KnowledgeAnswer(
                "Ainda não encontrei documentos, código ou índices relevantes neste workspace para responder com referência.",
                [],
                "none");
        }

        var prompt = BuildPrompt(question, references);

        try
        {
            var answer = await ollamaClient.GenerateAsync(prompt, cancellationToken);
            return new KnowledgeAnswer(answer, references, "ollama");
        }
        catch
        {
            return new KnowledgeAnswer(
                "Encontrei referências candidatas, mas o Ollama ainda não está pronto para gerar a resposta natural. Use as referências retornadas como contexto.",
                references,
                "fallback");
        }
    }

    private static string BuildPrompt(string question, IReadOnlyCollection<SearchResult> references)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Você é um assistente privado de conhecimento corporativo.");
        builder.AppendLine("Responda em português, de forma objetiva, usando apenas o contexto fornecido.");
        builder.AppendLine("Cite os arquivos de referência pelo caminho relativo quando usar uma informação.");
        builder.AppendLine();
        builder.AppendLine("Pergunta:");
        builder.AppendLine(question);
        builder.AppendLine();
        builder.AppendLine("Contexto:");

        foreach (var reference in references)
        {
            builder.AppendLine($"Fonte: {reference.RelativePath}");
            builder.AppendLine(reference.Snippet);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

