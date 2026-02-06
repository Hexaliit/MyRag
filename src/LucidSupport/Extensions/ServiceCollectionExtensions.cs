using LucidSupport.Models;
using LucidSupport.Services.Escalation;
using LucidSupport.Services.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidSupport.Extensions;

/// <summary>
///     DI registration for LucidSupport services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Register core runtime services (store, response engine, evaluators).
    /// </summary>
    public static IServiceCollection AddLucidSupportRuntime(
        this IServiceCollection services, LucidSupportConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton<IPageModelStore, PageModelStore>();
        services.AddSingleton<IResponseEngine, TemplateResponseEngine>();
        services.AddSingleton<ConditionEvaluator>();
        services.AddSingleton<WorkflowEvaluator>();
        return services;
    }

    /// <summary>
    ///     Register escalation plugins (console + optional webhook).
    /// </summary>
    public static IServiceCollection AddEscalationPlugins(this IServiceCollection services)
    {
        services.AddSingleton<IEscalationPlugin, ConsoleEscalationPlugin>();
        return services;
    }

    /// <summary>
    ///     Register manual knowledge ingestion services.
    ///     Loads *.knowledge.json files and wraps the response engine with augmented search.
    /// </summary>
    public static IServiceCollection AddManualKnowledge(
        this IServiceCollection services, string manualDir)
    {
        services.AddSingleton<Services.Knowledge.ManualKnowledgeStore>();
        services.AddSingleton<Services.Knowledge.ManualChunker>();

        // Register ONNX embedding service for knowledge search (initialized via hosted service)
        services.AddSingleton(new Mostlylucid.DocSummarizer.Services.Onnx.OnnxEmbeddingService(
            new Mostlylucid.DocSummarizer.Config.OnnxConfig(), verbose: false));
        services.AddSingleton<IEmbeddingService>(sp =>
            sp.GetRequiredService<Mostlylucid.DocSummarizer.Services.Onnx.OnnxEmbeddingService>());
        services.AddHostedService<OnnxInitHostedService>();

        // Decorate IResponseEngine with AugmentedResponseEngine
        services.Decorate<IResponseEngine>((inner, sp) =>
            new AugmentedResponseEngine(
                inner,
                sp.GetRequiredService<Services.Knowledge.ManualKnowledgeStore>(),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<ILogger<AugmentedResponseEngine>>()));

        return services;
    }

    /// <summary>
    ///     Register AI feedback services (Ollama client, intent classifier, AI response engine).
    /// </summary>
    public static IServiceCollection AddAiFeedback(
        this IServiceCollection services, LucidSupportConfig config)
    {
        services.AddSingleton(sp => new Services.AI.SupportOllamaClient(
            config.OllamaBaseUrl, config.SentinelModel, config.SentinelMaxTokens,
            sp.GetRequiredService<ILogger<Services.AI.SupportOllamaClient>>()));
        services.AddSingleton<Services.AI.IntentClassifier>();

        // Decorate IResponseEngine with AiResponseEngine
        services.Decorate<IResponseEngine>((inner, sp) =>
            new AiResponseEngine(
                inner,
                sp.GetRequiredService<Services.AI.SupportOllamaClient>(),
                sp.GetRequiredService<Services.AI.IntentClassifier>(),
                sp.GetRequiredService<ILogger<AiResponseEngine>>()));

        return services;
    }
}

/// <summary>
///     Simple decorator extension for IServiceCollection. Replaces the existing registration
///     of TInterface with a factory that wraps it.
/// </summary>
internal static class DecoratorExtensions
{
    public static IServiceCollection Decorate<TInterface>(
        this IServiceCollection services,
        Func<TInterface, IServiceProvider, TInterface> decorator)
        where TInterface : class
    {
        var wrappedDescriptor = services.LastOrDefault(s => s.ServiceType == typeof(TInterface));
        if (wrappedDescriptor is null)
            throw new InvalidOperationException($"No registration for {typeof(TInterface).Name} found to decorate.");

        // Build factory that resolves the inner implementation, then wraps it
        var objectFactory = wrappedDescriptor.ImplementationFactory;
        var implementationType = wrappedDescriptor.ImplementationType;
        var implementationInstance = wrappedDescriptor.ImplementationInstance;

        services.Remove(wrappedDescriptor);

        services.AddSingleton(sp =>
        {
            TInterface inner;
            if (implementationInstance is not null)
                inner = (TInterface)implementationInstance;
            else if (objectFactory is not null)
                inner = (TInterface)objectFactory(sp);
            else if (implementationType is not null)
                inner = (TInterface)ActivatorUtilities.CreateInstance(sp, implementationType);
            else
                throw new InvalidOperationException($"Cannot resolve inner {typeof(TInterface).Name}.");

            return decorator(inner, sp);
        });

        return services;
    }
}

/// <summary>
///     Initializes the ONNX embedding service on application startup without blocking DI.
/// </summary>
internal sealed class OnnxInitHostedService(
    Mostlylucid.DocSummarizer.Services.Onnx.OnnxEmbeddingService onnxService,
    ILogger<OnnxInitHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("Initializing ONNX embedding service...");
        await onnxService.InitializeAsync(ct);
        logger.LogInformation("ONNX embedding service ready");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
