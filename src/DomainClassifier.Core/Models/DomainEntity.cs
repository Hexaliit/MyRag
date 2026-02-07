namespace DomainClassifier.Core.Models;

/// <summary>
///     A domain-specific named entity extracted from text.
/// </summary>
public record DomainEntity(
    string Text,
    string EntityType,
    string DomainId,
    int StartOffset,
    int EndOffset,
    double Confidence,
    Dictionary<string, object?>? Metadata = null);
