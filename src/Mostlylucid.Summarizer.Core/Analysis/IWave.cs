namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
///     Unified wave interface. Everything is a wave: sources, analysis, destinations, UI.
///     Waves consume signals from context and produce new signals.
/// </summary>
public interface IWave
{
    /// <summary>
    ///     Unique wave name.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Execute the wave, consuming signals from context and producing new signals.
    /// </summary>
    Task<IReadOnlyList<Signal>> ExecuteAsync(WaveContext context, CancellationToken ct = default);
}