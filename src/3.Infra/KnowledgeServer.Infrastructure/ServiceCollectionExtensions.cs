using KnowledgeServer.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeServer.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeServerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<WorkspaceOptions>(
            configuration.GetSection(WorkspaceOptions.SectionName));
        services.Configure<OllamaOptions>(
            configuration.GetSection(OllamaOptions.SectionName));
        services.Configure<QdrantOptions>(
            configuration.GetSection(QdrantOptions.SectionName));
        services.Configure<ModelOptions>(
            configuration.GetSection(ModelOptions.SectionName));

        services.AddSingleton<IWorkspaceStore, FileSystemWorkspaceStore>();
        services.AddSingleton<ICodeIntelligenceService, DeterministicCodeIntelligenceService>();
        services.AddSingleton<IGraphifyService, ProcessGraphifyService>();
        services.AddHttpClient<OllamaClient>();
        services.AddHttpClient<QdrantClient>();
        services.AddSingleton<IKnowledgeSearch, VectorKnowledgeSearch>();
        services.AddSingleton<IKnowledgeGenerator, OllamaKnowledgeGenerator>();
        services.AddSingleton<IIndexingPipeline, IndexingPipeline>();
        services.AddSingleton<KnowledgeChatService>();

        return services;
    }
}
