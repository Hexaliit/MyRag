namespace DomainClassifier.Core.Models;

/// <summary>
///     Result of classifying text against a domain.
/// </summary>
public record DomainClassification(
    string DomainId,
    string DomainName,
    double Confidence,
    Dictionary<string, object?>? Metadata = null);
