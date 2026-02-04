namespace LucidRAG.LLM.Config;

/// <summary>
///     LLM backend types supported by the provider infrastructure.
/// </summary>
public enum LlmBackendType
{
    /// <summary>Local Ollama server</summary>
    Ollama,

    /// <summary>Anthropic Claude API</summary>
    Anthropic,

    /// <summary>OpenAI API (including Azure OpenAI)</summary>
    OpenAI,

    /// <summary>LMStudio local server (OpenAI-compatible)</summary>
    LMStudio,

    /// <summary>Local GGUF inference via LLamaSharp (no external server needed)</summary>
    LLamaSharp
}

/// <summary>
///     Provider tier for routing decisions.
/// </summary>
public enum ProviderTier
{
    /// <summary>Fast local model for triage and classification</summary>
    Triage,

    /// <summary>Balanced cost/quality for general tasks</summary>
    General,

    /// <summary>High quality for complex reasoning</summary>
    Synthesis,

    /// <summary>Vision-capable model for image analysis</summary>
    Vision,

    /// <summary>Cost-effective fallback</summary>
    Fallback
}

/// <summary>
///     Strategy for selecting among multiple endpoints in a load-balanced backend.
/// </summary>
public enum EndpointSelectionStrategy
{
    /// <summary>Distribute requests evenly across endpoints</summary>
    RoundRobin,

    /// <summary>Prefer the endpoint with the lowest observed response time</summary>
    Fastest
}