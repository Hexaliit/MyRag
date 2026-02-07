using Microsoft.Extensions.Logging;

namespace DomainClassifier.Core.Services.Onnx;

/// <summary>
///     ONNX-based token classification (NER) service.
///     Stub implementation for Phase 2 - NER models require fine-tuned ONNX exports.
/// </summary>
public class OnnxNerService : IOnnxNerService
{
    private readonly ILogger<OnnxNerService> _logger;

    public OnnxNerService(ILogger<OnnxNerService> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(ClassifierModelInfo model, CancellationToken ct = default)
    {
        _logger.LogDebug("NER service initialization requested for {ModelName} - not yet implemented",
            model.Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NerEntity>> ExtractAsync(
        ClassifierModelInfo model,
        string text,
        CancellationToken ct = default)
    {
        _logger.LogDebug("NER extraction requested - returning empty (Phase 2 feature)");
        return Task.FromResult<IReadOnlyList<NerEntity>>([]);
    }
}
