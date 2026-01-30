using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Caching;
using LucidRAG.Decomposer.Glossary;
using LucidRAG.Decomposer.KnowledgeBase;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using LucidRAG.Decomposer.Refinement;
using Microsoft.Extensions.DependencyInjection;

namespace LucidRAG.Decomposer.Integration;

/// <summary>
/// DI registration for the decomposer pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the decomposition pipeline and all its components.
    /// </summary>
    public static IServiceCollection AddDecomposer(this IServiceCollection services)
    {
        // Concept registry (plugins can customize via IServiceProvider)
        services.AddSingleton<ConceptRegistry>();

        // Analysis
        services.AddSingleton<ComplexityClassifier>();
        services.AddSingleton<ConceptClassifier>();
        services.AddSingleton<IQueryAnalyzer, ReferenceExtractor>();
        services.AddSingleton<IQueryAnalyzer, StructuralAnalyzer>();
        services.AddSingleton<IQueryAnalyzer, EntityRelationAnalyzer>();
        services.AddSingleton<IQueryAnalyzer, TemporalAnalyzer>();
        services.AddSingleton<IQueryAnalyzer, SemanticClusterAnalyzer>();
        services.AddSingleton<IQueryAnalyzer, ToolUseAnalyzer>();

        // Refinement
        services.AddSingleton<SentinelRefiner>();
        services.AddSingleton<DeterministicRefiner>();
        // Default to SentinelRefiner — falls back to DeterministicRefiner internally
        services.AddSingleton<IDecompositionRefiner, SentinelRefiner>();

        // Caching
        services.AddSingleton<IDecompositionCache, InMemoryDecompositionCache>();

        // Orchestration
        services.AddSingleton<DecompositionPipeline>();
        services.AddSingleton<DecompositionOrchestrator>();
        services.AddSingleton<RecursionGuard>();

        // Glossary
        services.AddSingleton<GlossaryService>();

        return services;
    }
}
