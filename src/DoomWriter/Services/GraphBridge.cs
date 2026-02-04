using System.Text.Json;
using DoomWriter.Models;

namespace DoomWriter.Services;

public class GraphBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private Func<string, Task<string?>>? _invokeScript;

    public void SetInvokeScript(Func<string, Task<string?>> invokeScript)
    {
        _invokeScript = invokeScript;
    }

    // --- C# → JS commands ---

    public async Task SetGraphDataAsync(GraphData data)
    {
        if (_invokeScript == null) return;
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await _invokeScript($"setGraphData({json})");
    }

    public async Task AddNodesAsync(List<GraphNode> nodes)
    {
        if (_invokeScript == null || nodes.Count == 0) return;
        var json = JsonSerializer.Serialize(nodes, JsonOptions);
        await _invokeScript($"addNodes({json})");
    }

    public async Task AddEdgesAsync(List<GraphEdge> edges)
    {
        if (_invokeScript == null || edges.Count == 0) return;
        var json = JsonSerializer.Serialize(edges, JsonOptions);
        await _invokeScript($"addEdges({json})");
    }

    public async Task FocusNodeAsync(string nodeId)
    {
        if (_invokeScript == null) return;
        var escaped = JsonSerializer.Serialize(nodeId);
        await _invokeScript($"focusNode({escaped})");
    }

    public async Task SetFilterAsync(string? entityType)
    {
        if (_invokeScript == null) return;
        var escaped = JsonSerializer.Serialize(entityType);
        await _invokeScript($"setFilter({escaped})");
    }

    public async Task SetThemeAsync(string theme)
    {
        if (_invokeScript == null) return;
        var escaped = JsonSerializer.Serialize(theme);
        await _invokeScript($"setTheme({escaped})");
    }

    public async Task ClearGraphAsync()
    {
        if (_invokeScript == null) return;
        await _invokeScript("clearGraph()");
    }

    // --- JS → C# events ---

    public event EventHandler<string>? NodeClicked;
    public event EventHandler<string>? NodeDoubleClicked;
    public event EventHandler<string>? NodeExpanded;
    public event EventHandler? GraphReady;

    public void HandleWebMessage(string messageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "nodeClicked":
                    var clickedId = root.GetProperty("nodeId").GetString() ?? "";
                    NodeClicked?.Invoke(this, clickedId);
                    break;

                case "nodeDoubleClicked":
                    var dblClickId = root.GetProperty("nodeId").GetString() ?? "";
                    NodeDoubleClicked?.Invoke(this, dblClickId);
                    break;

                case "nodeExpanded":
                    var expandId = root.GetProperty("nodeId").GetString() ?? "";
                    NodeExpanded?.Invoke(this, expandId);
                    break;

                case "graphReady":
                    GraphReady?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }
}