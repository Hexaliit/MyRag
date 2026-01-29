namespace Mostlylucid.DocSummarizer.Images.Models.Dynamic;

/// <summary>
/// Image-specific signal extending the shared Signal with ValueType for serialization.
/// All 50+ image analysis waves produce these signals.
/// Base Signal, AggregationStrategy, and SignalTags come from Mostlylucid.Summarizer.Core.Analysis
/// via global using in GlobalUsings.cs.
/// </summary>
public record Signal : Mostlylucid.Summarizer.Core.Analysis.Signal
{
    /// <summary>
    /// Data type of the value for serialization/deserialization.
    /// Image pipeline uses this for type-safe JSON round-tripping.
    /// </summary>
    public string? ValueType { get; init; }
}
