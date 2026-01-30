namespace LucidRAG.LLM.Services.LoadBalancing;

/// <summary>
/// Strategy for selecting an endpoint from a pool of available endpoints.
/// </summary>
public interface IEndpointSelector
{
    /// <summary>
    /// Select the next endpoint to use. Returns null if no healthy endpoints available.
    /// </summary>
    EndpointState? Select(IReadOnlyList<EndpointState> endpoints);
}
