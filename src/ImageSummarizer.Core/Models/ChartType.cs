namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
///     Specific chart/graph type classification for data visualization images.
/// </summary>
public enum ChartType
{
    /// <summary>
    ///     Unable to determine chart type
    /// </summary>
    Unknown,

    /// <summary>
    ///     Vertical or horizontal bar chart
    /// </summary>
    BarChart,

    /// <summary>
    ///     Line chart with connected data points
    /// </summary>
    LineChart,

    /// <summary>
    ///     Pie or donut chart with circular segments
    /// </summary>
    PieChart,

    /// <summary>
    ///     Scatter plot with individual data points
    /// </summary>
    ScatterPlot,

    /// <summary>
    ///     Area chart (filled area under line)
    /// </summary>
    AreaChart,

    /// <summary>
    ///     Histogram showing frequency distribution
    /// </summary>
    Histogram,

    /// <summary>
    ///     Box plot / box-and-whisker plot for statistical distribution
    /// </summary>
    BoxPlot,

    /// <summary>
    ///     Heatmap with color-coded values
    /// </summary>
    Heatmap,

    /// <summary>
    ///     Radar/spider chart with radial axes
    /// </summary>
    RadarChart,

    /// <summary>
    ///     Waterfall chart showing cumulative effect
    /// </summary>
    WaterfallChart,

    /// <summary>
    ///     Treemap with nested rectangles
    /// </summary>
    Treemap,

    /// <summary>
    ///     Sankey diagram showing flow between nodes
    /// </summary>
    SankeyDiagram,

    /// <summary>
    ///     Gauge/meter visualization
    /// </summary>
    Gauge,

    /// <summary>
    ///     Other chart type not in this list
    /// </summary>
    Other
}