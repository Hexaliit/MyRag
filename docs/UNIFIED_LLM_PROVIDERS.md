# Unified LLM Provider Infrastructure

This document describes the unified LLM provider infrastructure in ***lucid***RAG, which provides a consistent interface for working with multiple LLM backends (OpenAI, Anthropic, Ollama, LMStudio) through YAML-based configuration.

## Overview

The `LucidRAG.LLM` project provides:

- **Named Providers**: Semantic aliases like `fast-local`, `general`, `smart`, `vision`
- **YAML Configuration**: Define backends, models, and providers in `llm-providers.yaml`
- **Named Prompt Library**: Reusable prompts with variable substitution in `prompts.yaml`
- **Polly Resilience**: Automatic retry with exponential backoff and circuit breaker
- **OpenTelemetry Observability**: Distributed tracing and metrics for monitoring

## Quick Start

### Basic Usage

```csharp
// Inject the factory
public class MyService
{
    private readonly ILlmProviderFactory _llmFactory;

    public MyService(ILlmProviderFactory llmFactory)
    {
        _llmFactory = llmFactory;
    }

    public async Task<string> GenerateAsync(string prompt)
    {
        // Get provider by tier
        var provider = _llmFactory.GetProviderForTier("general");
        return await provider.GenerateAsync(prompt);
    }
}
```

### Using Named Prompts

```csharp
// Use a named prompt from prompts.yaml
var provider = _llmFactory.GetProviderForTier("general");
var result = await provider.GenerateWithPromptAsync(
    "rag_synthesis",
    new Dictionary<string, object>
    {
        ["segments"] = "Context from retrieved documents...",
        ["question"] = "What is the main topic?"
    });
```

### Generating JSON

```csharp
// Get structured JSON output
var provider = _llmFactory.GetProviderForTier("triage");
var result = await provider.GenerateJsonWithPromptAsync<QueryDecomposition>(
    "query_decomposition",
    new Dictionary<string, object>
    {
        ["query"] = "How does authentication work in the system?",
        ["context"] = "User is asking about security features"
    });

public class QueryDecomposition
{
    public List<SubQuery> SubQueries { get; set; } = new();
}

public class SubQuery
{
    public string Query { get; set; } = "";
    public string Purpose { get; set; } = "";
    public int Priority { get; set; }
}
```

---

## Configuration

### llm-providers.yaml

Located at `src/LucidRAG/Config/llm-providers.yaml`:

```yaml
# Backend definitions - connection settings for each LLM service
backends:
  ollama-local:
    type: ollama
    base_url: http://localhost:11434
    timeout_seconds: 120
    enabled: true
    # Resilience settings
    max_retries: 3
    initial_retry_delay_ms: 500
    max_retry_delay_ms: 30000
    circuit_breaker_threshold: 5
    circuit_breaker_duration_seconds: 30

  anthropic:
    type: anthropic
    base_url: https://api.anthropic.com
    api_key: ${ANTHROPIC_API_KEY}  # Environment variable substitution
    api_version: "2023-06-01"
    timeout_seconds: 120
    enabled: true
    max_retries: 3
    initial_retry_delay_ms: 1000
    max_retry_delay_ms: 60000

  openai:
    type: openai
    base_url: https://api.openai.com/v1
    api_key: ${OPENAI_API_KEY}
    timeout_seconds: 120
    enabled: true

  lmstudio:
    type: openai  # LMStudio uses OpenAI-compatible API
    base_url: http://localhost:1234/v1
    api_key: ""
    timeout_seconds: 120
    enabled: false

# Model registry - define models once, reference by name
models:
  claude-opus:
    backend: anthropic
    model: claude-3-5-opus-latest
    context_window: 200000
    max_tokens: 8192
    temperature: 0.3

  claude-sonnet:
    backend: anthropic
    model: claude-3-5-sonnet-latest
    context_window: 200000
    max_tokens: 8192
    temperature: 0.3

  claude-haiku:
    backend: anthropic
    model: claude-3-5-haiku-latest
    context_window: 200000
    max_tokens: 4096
    temperature: 0.3

  gpt-4o:
    backend: openai
    model: gpt-4o
    context_window: 128000
    max_tokens: 4096
    temperature: 0.3
    capabilities:
      - vision

  gpt-4o-mini:
    backend: openai
    model: gpt-4o-mini
    context_window: 128000
    max_tokens: 4096
    temperature: 0.3

  qwen:
    backend: ollama-local
    model: qwen2.5:3b
    context_window: 8192
    max_tokens: 4096
    temperature: 0.3

  llama3:
    backend: ollama-local
    model: llama3.2:3b
    context_window: 8192
    max_tokens: 4096
    temperature: 0.3

  tinyllama:
    backend: ollama-local
    model: tinyllama
    context_window: 2048
    max_tokens: 1024
    temperature: 0.3

  minicpm-v:
    backend: ollama-local
    model: minicpm-v:8b
    context_window: 8192
    max_tokens: 4096
    capabilities:
      - vision

# Named providers - semantic aliases for specific use cases
providers:
  fast-local:
    model: tinyllama
    description: "Fast local model for triage and classification"

  local:
    model: qwen
    fallback: llama3
    description: "Local model for general tasks"

  general:
    model: claude-sonnet
    fallback: gpt-4o-mini
    description: "Balanced cost/quality for general tasks"

  smart:
    model: claude-opus
    fallback: gpt-4o
    description: "High quality for complex reasoning"

  vision:
    model: minicpm-v
    fallback: gpt-4o
    description: "Vision-capable model for image analysis"

  budget-cloud:
    model: claude-haiku
    fallback: gpt-4o-mini
    description: "Cost-effective cloud inference"

# Default provider assignments by task tier
defaults:
  triage: fast-local      # Quick classification, sentinel queries
  general: general        # Standard RAG queries, summarization
  synthesis: smart        # Complex synthesis, agentic tasks
  vision: vision          # Image analysis
  fallback: budget-cloud  # When primary fails
```

### Environment Variables

API keys can be specified using `${ENV_VAR}` syntax:

```yaml
api_key: ${ANTHROPIC_API_KEY}
```

Set the environment variable before running:

```bash
# Windows
set ANTHROPIC_API_KEY=sk-ant-...

# Linux/macOS
export ANTHROPIC_API_KEY=sk-ant-...
```

---

## Named Prompt Library (prompts.yaml)

Located at `src/LucidRAG/Config/prompts.yaml`:

### Available Prompts

| Prompt Name | Purpose | JSON Output | Provider |
|-------------|---------|-------------|----------|
| `query_decomposition` | Decompose complex queries into sub-queries | Yes | Any |
| `rag_synthesis` | Synthesize answers from retrieved segments | No | Any |
| `entity_extraction` | Extract entities and relationships for GraphRAG | Yes | Any |
| `document_classification` | Classify document type and characteristics | Yes | fast-local |
| `image_caption` | Generate WCAG-compliant alt-text captions | No | vision |
| `query_clarification` | Clarify ambiguous user queries | Yes | Any |
| `summary` | Generate document summaries | No | Any |

### Prompt Structure

```yaml
prompts:
  query_decomposition:
    name: query_decomposition
    description: "Decompose complex queries into sub-queries"
    version: 1
    json_output: true

    system: |
      You are a query analyst. Your task is to decompose complex queries into simpler,
      focused sub-queries that can be independently answered and then merged to form
      a comprehensive answer.

    template: |
      Query: {query}

      Additional context:
      {context}

      Decompose this query into 2-5 focused sub-queries.
      Return JSON in this format:
      {
        "sub_queries": [
          {"query": "...", "purpose": "...", "priority": 1}
        ]
      }

    overrides:
      anthropic:
        max_tokens: 2048
      ollama:
        temperature: 0.2
```

### Template Variables

Use `{variable_name}` syntax in templates. Variables are substituted at runtime:

```csharp
var result = await provider.GenerateWithPromptAsync(
    "rag_synthesis",
    new Dictionary<string, object>
    {
        ["segments"] = formattedSegments,
        ["question"] = userQuery
    });
```

For a complete reference of all available variables by task type, see [PROMPT_TEMPLATE_VARIABLES.md](PROMPT_TEMPLATE_VARIABLES.md).

### Provider-Specific Overrides

Each prompt can override settings per backend:

```yaml
overrides:
  anthropic:
    max_tokens: 4096
  openai:
    max_tokens: 4096
  ollama:
    temperature: 0.1
    max_tokens: 2048
```

---

## Provider Tiers

The system supports tiered provider selection for different use cases:

| Tier | Use Case | Default Provider |
|------|----------|------------------|
| `triage` | Quick classification, sentinel queries | `fast-local` (tinyllama) |
| `general` | Standard RAG queries, summarization | `general` (claude-sonnet) |
| `synthesis` | Complex synthesis, agentic tasks | `smart` (claude-opus) |
| `vision` | Image analysis, OCR verification | `vision` (minicpm-v) |
| `fallback` | When primary provider fails | `budget-cloud` (claude-haiku) |

### Using Tiers

```csharp
// By tier enum
var provider = _llmFactory.GetProviderForTier(ProviderTier.General);

// By tier string
var provider = _llmFactory.GetProviderForTier("synthesis");

// Get specific named provider
var provider = _llmFactory.GetProvider("fast-local");

// Get default (general tier)
var provider = _llmFactory.GetDefault();
```

---

## Resilience (Polly)

All providers include automatic resilience via Polly:

### Retry Policy

- **Exponential backoff** with jitter
- Configurable per-backend in YAML:
  - `max_retries`: Maximum retry attempts (default: 3)
  - `initial_retry_delay_ms`: Initial delay before first retry (default: 500ms)
  - `max_retry_delay_ms`: Maximum delay between retries (default: 30000ms)

### Circuit Breaker

Prevents overwhelming failing backends:

- Opens after `circuit_breaker_threshold` failures (default: 5)
- Stays open for `circuit_breaker_duration_seconds` (default: 30s)
- Automatically tests and closes when backend recovers

### Handled Exceptions

- `HttpRequestException` - Network failures
- `TaskCanceledException` - Timeouts
- `TimeoutException` - Request timeouts
- `InvalidOperationException` - Service unavailable, rate limits

### Example Configuration

```yaml
backends:
  anthropic:
    # ... connection settings ...

    # Resilience for cloud APIs (longer delays for rate limits)
    max_retries: 3
    initial_retry_delay_ms: 1000
    max_retry_delay_ms: 60000
    circuit_breaker_threshold: 5
    circuit_breaker_duration_seconds: 30
```

---

## Observability (OpenTelemetry)

The provider infrastructure emits OpenTelemetry signals for monitoring.

### Activity Tracing

ActivitySource: `LucidRAG.LLM`

| Activity Name | Description | Tags |
|---------------|-------------|------|
| `LlmGenerate` | Text generation | provider, model, backend, prompt_length, response_length |
| `LlmGenerateJson` | JSON generation | provider, model, return_type |
| `LlmGenerateWithPrompt` | Named prompt generation | provider, prompt_name |
| `LlmGenerateJsonWithPrompt` | Named prompt JSON generation | provider, prompt_name, return_type |
| `LlmAvailabilityCheck` | Provider health check | provider, available |

### Metrics

Meter: `LucidRAG.LLM`

| Metric | Type | Description |
|--------|------|-------------|
| `lucidrag.llm.requests` | Counter | Total generation requests (by provider, status) |
| `lucidrag.llm.json_requests` | Counter | Total JSON generation requests |
| `lucidrag.llm.named_prompts` | Counter | Named prompt usage (by provider, prompt) |
| `lucidrag.llm.errors` | Counter | Errors by type (by provider, error_type) |
| `lucidrag.llm.retries` | Counter | Retry attempts (by provider, attempt) |
| `lucidrag.llm.circuit_breaker` | Counter | Circuit breaker state changes (by provider, state) |
| `lucidrag.llm.duration` | Histogram | Request duration in milliseconds |
| `lucidrag.llm.prompt_tokens` | Histogram | Estimated prompt tokens |
| `lucidrag.llm.response_tokens` | Histogram | Estimated response tokens |

### Integration with OpenTelemetry

```csharp
// In Program.cs
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("LucidRAG.LLM")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("LucidRAG.LLM")
        .AddOtlpExporter());
```

---

## Service Registration

### DI Registration

In `Program.cs`:

```csharp
using LucidRAG.LLM.Extensions;

// Register with YAML file paths
builder.Services.AddLucidRagLlm(
    Path.Combine(AppContext.BaseDirectory, "Config", "llm-providers.yaml"),
    Path.Combine(AppContext.BaseDirectory, "Config", "prompts.yaml"));

// Or use default Config directory
builder.Services.AddLucidRagLlm("Config");

// Or bind from IConfiguration (appsettings.json)
builder.Services.AddLucidRagLlm(builder.Configuration);
```

### Registered Services

| Interface | Implementation | Lifetime |
|-----------|----------------|----------|
| `ILlmProviderFactory` | `LlmProviderFactory` | Singleton |
| `IPromptService` | `PromptService` | Singleton |
| `ILlmService` | Default provider | Singleton |

---

## Interfaces

### INamedLlmProvider

Extends `ILlmService` with named provider features:

```csharp
public interface INamedLlmProvider : ILlmService
{
    string Name { get; }
    LlmBackendType BackendType { get; }
    string ModelId { get; }
    bool SupportsVision { get; }

    Task<string> GenerateWithPromptAsync(
        string promptName,
        Dictionary<string, object> variables,
        LlmOptions? options = null,
        CancellationToken ct = default);

    Task<T?> GenerateJsonWithPromptAsync<T>(
        string promptName,
        Dictionary<string, object> variables,
        LlmOptions? options = null,
        CancellationToken ct = default) where T : class;
}
```

### ILlmProviderFactory

Factory for resolving named providers:

```csharp
public interface ILlmProviderFactory
{
    INamedLlmProvider GetProvider(string name);
    bool TryGetProvider(string name, out INamedLlmProvider? provider);
    INamedLlmProvider GetProviderForTier(ProviderTier tier);
    INamedLlmProvider GetProviderForTier(string tierName);
    IReadOnlyList<string> GetProviderNames();
    bool HasProvider(string name);
    INamedLlmProvider GetDefault();
}
```

### IPromptService

Prompt resolution and rendering:

```csharp
public interface IPromptService
{
    PromptDefinition? GetPrompt(string name);
    (string? systemPrompt, string template) RenderPrompt(
        string promptName,
        Dictionary<string, object> variables,
        LlmBackendType backend);
    LlmOptions GetOptionsForPrompt(string promptName, LlmBackendType backend);
    IReadOnlyList<string> GetPromptNames();
}
```

---

## Adding Custom Prompts

Add new prompts to `prompts.yaml`:

```yaml
prompts:
  my_custom_prompt:
    name: my_custom_prompt
    description: "My custom prompt for specific task"
    version: 1
    json_output: false

    system: |
      You are a helpful assistant specialized in...

    template: |
      Input: {input}

      Please analyze the above and provide...

    overrides:
      anthropic:
        max_tokens: 2048
        temperature: 0.5
```

Then use it in code:

```csharp
var result = await provider.GenerateWithPromptAsync(
    "my_custom_prompt",
    new Dictionary<string, object>
    {
        ["input"] = myInputData
    });
```

---

## Adding Custom Backends

Add new backends to `llm-providers.yaml`:

```yaml
backends:
  my-custom-ollama:
    type: ollama
    base_url: http://my-gpu-server:11434
    timeout_seconds: 300
    enabled: true
    max_retries: 5

models:
  my-custom-model:
    backend: my-custom-ollama
    model: mixtral:8x7b
    context_window: 32000
    max_tokens: 4096
    temperature: 0.3

providers:
  my-custom-provider:
    model: my-custom-model
    description: "My custom Mixtral provider"
```

---

## Backward Compatibility

The infrastructure maintains backward compatibility with existing `ILlmService` usage:

```csharp
// This still works - returns the default provider
public class LegacyService
{
    private readonly ILlmService _llmService;

    public LegacyService(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<string> Generate(string prompt)
    {
        return await _llmService.GenerateAsync(prompt);
    }
}
```

---

## Related Documentation

- [Prompt Template Variables](PROMPT_TEMPLATE_VARIABLES.md) - Complete variable reference for custom prompts
- [YAML Manifest System](yaml-manifest-system.md) - Wave/lens manifest configuration
- [Sentinel Query Decomposition](DESIGN_SentinelLLM.md) - Agentic query processing
- [Deduplication Strategy](DEDUPLICATION_STRATEGY.md) - Segment deduplication

---

## Troubleshooting

### Provider Not Found

```
KeyNotFoundException: LLM provider 'xyz' not found. Available: fast-local, general, smart, vision
```

**Solution**: Check that the provider is defined in `llm-providers.yaml` and its backend is enabled.

### Circuit Breaker Open

```
InvalidOperationException: Provider 'general' is temporarily unavailable (circuit breaker open)
```

**Solution**: The backend has failed consistently. Wait for the circuit breaker duration to expire, or check the backend's health.

### Missing API Key

```
Warning: Anthropic service not registered in DI, skipping
```

**Solution**: Ensure the environment variable (e.g., `ANTHROPIC_API_KEY`) is set.

### YAML Parse Errors

```
Failed to load YAML config from Config/llm-providers.yaml
```

**Solution**: Validate YAML syntax. Common issues:
- Incorrect indentation (use spaces, not tabs)
- Missing colons after keys
- Unquoted special characters
