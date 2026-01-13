using LucidRAG.LLM.Config;

namespace LucidRAG.LLM.Services;

/// <summary>
/// Factory for resolving named LLM providers.
/// </summary>
public interface ILlmProviderFactory
{
    /// <summary>
    /// Get a provider by name (e.g., "fast-local", "smart", "vision").
    /// </summary>
    /// <param name="name">Provider name from configuration</param>
    /// <returns>Named provider instance</returns>
    /// <exception cref="KeyNotFoundException">If provider not found</exception>
    INamedLlmProvider GetProvider(string name);

    /// <summary>
    /// Try to get a provider by name.
    /// </summary>
    /// <param name="name">Provider name from configuration</param>
    /// <param name="provider">Provider instance if found</param>
    /// <returns>True if provider exists</returns>
    bool TryGetProvider(string name, out INamedLlmProvider? provider);

    /// <summary>
    /// Get provider for a specific tier (triage, general, synthesis, vision).
    /// </summary>
    /// <param name="tier">Provider tier</param>
    /// <returns>Provider for the tier</returns>
    INamedLlmProvider GetProviderForTier(ProviderTier tier);

    /// <summary>
    /// Get provider for a specific tier by name.
    /// </summary>
    /// <param name="tierName">Tier name (triage, general, synthesis, vision, fallback)</param>
    /// <returns>Provider for the tier</returns>
    INamedLlmProvider GetProviderForTier(string tierName);

    /// <summary>
    /// Get all registered provider names.
    /// </summary>
    IReadOnlyList<string> GetProviderNames();

    /// <summary>
    /// Check if a named provider exists.
    /// </summary>
    /// <param name="name">Provider name</param>
    /// <returns>True if provider is registered</returns>
    bool HasProvider(string name);

    /// <summary>
    /// Get the default provider (general tier).
    /// </summary>
    INamedLlmProvider GetDefault();
}
