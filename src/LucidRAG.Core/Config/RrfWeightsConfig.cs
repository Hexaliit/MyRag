namespace LucidRAG.Config;

public class RrfWeightsConfig
{
    public const string SectionName = "RrfWeights";

    /// <summary>
    ///     RRF smoothing constant (higher = more uniform ranking).
    ///     Default: 60 (standard RRF value).
    /// </summary>
    public int RrfK { get; set; } = 60;

    /// <summary>
    ///     Signal weights for hybrid search mode (dense + BM25 balanced).
    /// </summary>
    public RrfModeWeights Hybrid { get; set; } = new()
    {
        Dense = 1.0,
        Bm25 = 1.0,
        Salience = 0.3,
        Freshness = 0.2,
        Domain = 1.5,
        Venue = 0.8
    };

    /// <summary>
    ///     Signal weights for keyword search mode (BM25-dominant).
    /// </summary>
    public RrfModeWeights Keyword { get; set; } = new()
    {
        Dense = 0.3,
        Bm25 = 1.5,
        Salience = 0.2,
        Freshness = 0.1,
        Domain = 0.5,
        Venue = 0.3
    };
}

public class RrfModeWeights
{
    public double Dense { get; set; }
    public double Bm25 { get; set; }
    public double Salience { get; set; }
    public double Freshness { get; set; }
    public double Domain { get; set; }
    public double Venue { get; set; }
}
