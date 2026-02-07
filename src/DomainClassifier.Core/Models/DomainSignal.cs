namespace DomainClassifier.Core.Models;

/// <summary>
///     A domain-specific signal produced during enrichment.
/// </summary>
public record DomainSignal(
    string Key,
    object? Value,
    double Confidence,
    string Source,
    List<string>? Tags = null);
