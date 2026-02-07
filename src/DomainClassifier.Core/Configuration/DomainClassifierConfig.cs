namespace DomainClassifier.Core.Configuration;

/// <summary>
///     Configuration for the domain classifier system.
/// </summary>
public class DomainClassifierConfig
{
    public const string SectionName = "DomainClassifier";

    /// <summary>
    ///     Directory for storing downloaded ONNX models.
    ///     Defaults to {AppContext.BaseDirectory}/models.
    /// </summary>
    public string ModelDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>
    ///     Minimum confidence threshold for domain classification.
    ///     Plugins returning below this are ignored.
    /// </summary>
    public double ClassificationThreshold { get; set; } = 0.6;

    /// <summary>
    ///     Number of chunks to sample for domain classification.
    ///     Higher = more accurate but slower.
    /// </summary>
    public int ClassificationSampleSize { get; set; } = 5;

    /// <summary>
    ///     Maximum sequence length for ONNX classifier models.
    /// </summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>
    ///     Enable verbose logging of ONNX inference.
    /// </summary>
    public bool Verbose { get; set; }
}
