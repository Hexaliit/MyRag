# Workflow Graph Rendering (Running + Signal-Derived)

## Summary

Render live workflow runs and signal-derived workflows as interactive graphs in the LucidRAG UI. The feature produces a graph JSON payload per run (or per signal window), computes layout on the client using D3.js (already loaded), and renders as SVG. The timeline is derived from default signals so we can see what ran where, how long it ran, and the escalation path, without capturing everything. A document-level entry point lets users click a processed document to view the full flow of waves, steps, and coordinators that touched it. Retention is bounded by keeping a small number of traces (3-4) per document pipeline run.

---

## Goals

- Visualize running workflow progress (steps, state, timings, errors)
- Visualize signal-derived workflows when no explicit DAG exists
- Click a processed document to view the complete flow (sensors, waves, coordinators)
- Make it clear "what ran" and "which fields/signals were kept" that contributed to the ledger
- Show escalation paths (what triggered escalation, which tier ran next)
- Support a "live" view (updates as events arrive) and a "historical" view (replay a run)
- Keep signal payloads lightweight and avoid storing large artifacts in the graph
- Use the same trace data to optimize workflows (durations, escalation rates, signal-to-ledger mapping)

## Non-goals

- Full BPMN or workflow editing
- Independent long-term audit retention outside entity lifetime
- Storing large artifacts (images, embeddings, transcripts) in the graph store

---

## Current LucidRAG Context

### Existing Infrastructure We Can Leverage

| Component | Location | How We Use It |
|-----------|----------|---------------|
| **SignalR Hub** | `src/LucidRAG/Hubs/DocumentProcessingHub.cs` | Extend to broadcast workflow events |
| **D3.js v7** | `Views/Shared/_Layout.cshtml:41` | Use for graph layout and rendering |
| **Alpine.js State** | `Views/Home/Index.cshtml:1049-2393` | Add `workflowGraph` state object |
| **Graph View Mode** | `Index.cshtml:364-498` | Repurpose or add "Workflow" tab |
| **GraphController** | `Controllers/Api/GraphController.cs` | Pattern for new WorkflowGraphController |
| **EF Core + Postgres** | `Data/ApplicationDbContext.cs` | Add `WorkflowEvent` entity |
| **Processing Notification** | `Services/IProcessingNotificationService.cs` | Emit workflow events during processing |

### Current Processing Flow

```
DocumentProcessingService.ProcessDocumentAsync()
  ├── Stage: "Uploading" (0%)
  ├── Stage: "Processing content" (10-40%)
  │     └── DocSummarizer pipeline (waves)
  ├── Stage: "Extracting tables" (60-70%)
  │     └── TableProcessingService
  ├── Stage: "Extracting entities" (80%)
  │     └── GraphRagPipeline
  └── Stage: "Completed" (100%)
```

Each stage currently broadcasts via SignalR:
```csharp
await _notificationService.NotifyProgressAsync(
    collectionId, documentId, progress, stage);
```

---

## Conceptual Model

Two parallel views of the same run:

### 1) Definition-driven DAG
- Nodes are steps/waves (deterministic, known ahead of time)
- Edges are dependencies (required signals, explicit ordering)
- Runtime events only decorate nodes (status, timings, error)

### 2) Signal-derived DAG
- Nodes are steps and signals observed at runtime
- Edges are inferred: step emits signal, step consumes signal
- Useful when the workflow is dynamic or when the DAG is not declared

Both views resolve to the same graph JSON schema so the UI can switch between them.

---

## Data Model

### EF Core Entity: `WorkflowEvent`

**File:** `src/LucidRAG/Data/Entities/WorkflowEvent.cs`

```csharp
public class WorkflowEvent
{
    public Guid Id { get; set; }

    // Run identification
    public Guid RunId { get; set; }
    public string WorkflowKey { get; set; } = null!;  // e.g., "document-pipeline"

    // Entity linkage (nullable for global events)
    public Guid? CollectionId { get; set; }
    public Guid? DocumentId { get; set; }

    // Event details
    public string EventType { get; set; } = null!;    // workflow.step.started, etc.
    public string? CoordinatorKey { get; set; }       // e.g., "DocumentQueueProcessor"
    public string? StepKey { get; set; }              // e.g., "TableExtraction"
    public string? SignalKey { get; set; }            // e.g., "tables.extracted"

    // Timing
    public DateTimeOffset Timestamp { get; set; }
    public int? DurationMs { get; set; }

    // Status
    public WorkflowEventStatus Status { get; set; }   // Running, Success, Failed, Skipped

    // Payload (small, structured)
    public JsonDocument? Payload { get; set; }

    // Navigation
    public Document? Document { get; set; }
    public Collection? Collection { get; set; }

    // Indexes: (RunId, Timestamp), (DocumentId, Timestamp), (WorkflowKey, Timestamp)
}

public enum WorkflowEventStatus
{
    Running,
    Success,
    Failed,
    Skipped
}
```

### Event Types

```csharp
public static class WorkflowEventTypes
{
    public const string RunStarted = "workflow.run.started";
    public const string RunCompleted = "workflow.run.completed";
    public const string RunFailed = "workflow.run.failed";

    public const string StepStarted = "workflow.step.started";
    public const string StepCompleted = "workflow.step.completed";
    public const string StepFailed = "workflow.step.failed";
    public const string StepSkipped = "workflow.step.skipped";

    public const string SignalEmitted = "workflow.signal.emitted";
    public const string SignalRetracted = "workflow.signal.retracted";

    public const string EscalationTriggered = "workflow.escalation.triggered";
    public const string ArtifactLinked = "workflow.artifact.linked";
}
```

### Migration

**File:** `src/LucidRAG/Data/Migrations/YYYYMMDDHHMMSS_AddWorkflowEvents.cs`

```csharp
migrationBuilder.CreateTable(
    name: "WorkflowEvents",
    columns: table => new
    {
        Id = table.Column<Guid>(nullable: false),
        RunId = table.Column<Guid>(nullable: false),
        WorkflowKey = table.Column<string>(maxLength: 100, nullable: false),
        CollectionId = table.Column<Guid>(nullable: true),
        DocumentId = table.Column<Guid>(nullable: true),
        EventType = table.Column<string>(maxLength: 100, nullable: false),
        CoordinatorKey = table.Column<string>(maxLength: 100, nullable: true),
        StepKey = table.Column<string>(maxLength: 100, nullable: true),
        SignalKey = table.Column<string>(maxLength: 200, nullable: true),
        Timestamp = table.Column<DateTimeOffset>(nullable: false),
        DurationMs = table.Column<int>(nullable: true),
        Status = table.Column<int>(nullable: false),
        Payload = table.Column<JsonDocument>(type: "jsonb", nullable: true)
    });

// Performance indexes
migrationBuilder.CreateIndex("IX_WorkflowEvents_RunId_Timestamp", "WorkflowEvents",
    new[] { "RunId", "Timestamp" });
migrationBuilder.CreateIndex("IX_WorkflowEvents_DocumentId_Timestamp", "WorkflowEvents",
    new[] { "DocumentId", "Timestamp" });
migrationBuilder.CreateIndex("IX_WorkflowEvents_WorkflowKey_Timestamp", "WorkflowEvents",
    new[] { "WorkflowKey", "Timestamp" });
```

### Retention Policy

Keep last N traces per document:

```csharp
public class WorkflowEventRetentionService : BackgroundService
{
    private const int MaxTracesPerDocument = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CleanupOldTracesAsync();
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task CleanupOldTracesAsync()
    {
        // Get documents with more than MaxTracesPerDocument runs
        var documentsToClean = await _db.WorkflowEvents
            .Where(e => e.DocumentId != null && e.EventType == WorkflowEventTypes.RunStarted)
            .GroupBy(e => e.DocumentId)
            .Where(g => g.Count() > MaxTracesPerDocument)
            .Select(g => g.Key)
            .ToListAsync();

        foreach (var docId in documentsToClean)
        {
            var runsToKeep = await _db.WorkflowEvents
                .Where(e => e.DocumentId == docId && e.EventType == WorkflowEventTypes.RunStarted)
                .OrderByDescending(e => e.Timestamp)
                .Take(MaxTracesPerDocument)
                .Select(e => e.RunId)
                .ToListAsync();

            await _db.WorkflowEvents
                .Where(e => e.DocumentId == docId && !runsToKeep.Contains(e.RunId))
                .ExecuteDeleteAsync();
        }
    }
}
```

---

## Services

### IWorkflowEventSink Interface

**File:** `src/LucidRAG/Services/Workflow/IWorkflowEventSink.cs`

```csharp
public interface IWorkflowEventSink
{
    Task EmitAsync(WorkflowEvent @event, CancellationToken ct = default);

    // Convenience methods
    Task RunStartedAsync(Guid runId, string workflowKey, Guid? documentId, Guid? collectionId);
    Task RunCompletedAsync(Guid runId, int durationMs);
    Task RunFailedAsync(Guid runId, string error);

    Task StepStartedAsync(Guid runId, string stepKey, string? coordinatorKey = null);
    Task StepCompletedAsync(Guid runId, string stepKey, int durationMs);
    Task StepFailedAsync(Guid runId, string stepKey, string error);

    Task SignalEmittedAsync(Guid runId, string signalKey, object? payload = null);
    Task EscalationTriggeredAsync(Guid runId, string fromStep, string reason, string nextTier);
}
```

### Implementation: WorkflowEventSink

**File:** `src/LucidRAG/Services/Workflow/WorkflowEventSink.cs`

```csharp
public class WorkflowEventSink : IWorkflowEventSink
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<WorkflowHub, IWorkflowHubClient> _hub;
    private readonly ILogger<WorkflowEventSink> _logger;

    public async Task EmitAsync(WorkflowEvent @event, CancellationToken ct = default)
    {
        // Persist to database
        _db.WorkflowEvents.Add(@event);
        await _db.SaveChangesAsync(ct);

        // Broadcast to connected clients
        var groupName = @event.DocumentId.HasValue
            ? $"document_{@event.DocumentId}"
            : $"workflow_{@event.WorkflowKey}";

        await _hub.Clients.Group(groupName).OnWorkflowEvent(new WorkflowEventDto
        {
            RunId = @event.RunId,
            EventType = @event.EventType,
            StepKey = @event.StepKey,
            SignalKey = @event.SignalKey,
            Status = @event.Status.ToString().ToLowerInvariant(),
            Timestamp = @event.Timestamp,
            DurationMs = @event.DurationMs,
            Payload = @event.Payload
        });
    }

    // ... convenience method implementations
}
```

### WorkflowGraphBuilder

**File:** `src/LucidRAG/Services/Workflow/WorkflowGraphBuilder.cs`

```csharp
public class WorkflowGraphBuilder
{
    public WorkflowGraphDto BuildGraph(IEnumerable<WorkflowEvent> events, GraphViewMode mode)
    {
        var nodes = new List<WorkflowGraphNode>();
        var edges = new List<WorkflowGraphEdge>();
        var stepNodes = new Dictionary<string, WorkflowGraphNode>();
        var signalNodes = new Dictionary<string, WorkflowGraphNode>();

        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            switch (evt.EventType)
            {
                case WorkflowEventTypes.StepStarted:
                case WorkflowEventTypes.StepCompleted:
                case WorkflowEventTypes.StepFailed:
                    AddOrUpdateStepNode(stepNodes, evt);
                    break;

                case WorkflowEventTypes.SignalEmitted when mode != GraphViewMode.Definition:
                    AddSignalNode(signalNodes, evt, stepNodes, edges);
                    break;

                case WorkflowEventTypes.EscalationTriggered:
                    AddEscalationEdge(evt, stepNodes, edges);
                    break;
            }
        }

        nodes.AddRange(stepNodes.Values);
        if (mode != GraphViewMode.Definition)
            nodes.AddRange(signalNodes.Values);

        return new WorkflowGraphDto
        {
            GraphId = events.First().RunId.ToString(),
            Nodes = nodes,
            Edges = edges,
            Metadata = new WorkflowGraphMetadata
            {
                RunId = events.First().RunId,
                WorkflowKey = events.First().WorkflowKey,
                DocumentId = events.First().DocumentId,
                UpdatedAt = events.Max(e => e.Timestamp)
            }
        };
    }
}
```

---

## API Endpoints

### WorkflowGraphController

**File:** `src/LucidRAG/Controllers/Api/WorkflowGraphController.cs`

```csharp
[ApiController]
[Route("api/workflow")]
public class WorkflowGraphController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly WorkflowGraphBuilder _graphBuilder;

    /// <summary>
    /// List active and recent workflow runs
    /// </summary>
    [HttpGet("runs")]
    public async Task<ActionResult<PagedResult<WorkflowRunSummary>>> GetRuns(
        [FromQuery] WorkflowRunStatus? status = null,
        [FromQuery] Guid? documentId = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var query = _db.WorkflowEvents
            .Where(e => e.EventType == WorkflowEventTypes.RunStarted);

        if (status.HasValue)
            query = query.Where(e => /* filter by status */);
        if (documentId.HasValue)
            query = query.Where(e => e.DocumentId == documentId);

        // Return run summaries with latest status
    }

    /// <summary>
    /// Get graph for a specific workflow run
    /// </summary>
    [HttpGet("runs/{runId:guid}/graph")]
    public async Task<ActionResult<WorkflowGraphDto>> GetRunGraph(
        Guid runId,
        [FromQuery] GraphViewMode view = GraphViewMode.Merged)
    {
        var events = await _db.WorkflowEvents
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        if (!events.Any())
            return NotFound();

        return _graphBuilder.BuildGraph(events, view);
    }

    /// <summary>
    /// Get all workflow runs for a document
    /// </summary>
    [HttpGet("documents/{documentId:guid}/runs")]
    public async Task<ActionResult<List<WorkflowRunSummary>>> GetDocumentRuns(Guid documentId)
    {
        // Return list of runs for this document, most recent first
    }

    /// <summary>
    /// Get latest graph for a document
    /// </summary>
    [HttpGet("documents/{documentId:guid}/graph")]
    public async Task<ActionResult<WorkflowGraphDto>> GetDocumentGraph(
        Guid documentId,
        [FromQuery] GraphViewMode view = GraphViewMode.Merged)
    {
        var latestRunId = await _db.WorkflowEvents
            .Where(e => e.DocumentId == documentId && e.EventType == WorkflowEventTypes.RunStarted)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.RunId)
            .FirstOrDefaultAsync();

        if (latestRunId == Guid.Empty)
            return NotFound();

        return await GetRunGraph(latestRunId, view);
    }

    /// <summary>
    /// SSE stream for live workflow events
    /// </summary>
    [HttpGet("runs/{runId:guid}/events")]
    public async Task GetRunEventsStream(Guid runId, [FromQuery] DateTimeOffset? since = null)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        // Stream events as SSE
    }
}
```

### Graph JSON Schema

```json
{
  "graphId": "run-abc123",
  "nodes": [
    {
      "id": "step:ContentExtraction",
      "label": "Content Extraction",
      "type": "step",
      "status": "success",
      "durationMs": 1234,
      "coordinator": "DocumentQueueProcessor",
      "startedAt": "2026-01-12T12:34:56Z",
      "completedAt": "2026-01-12T12:34:57Z"
    },
    {
      "id": "step:TableExtraction",
      "label": "Table Extraction",
      "type": "step",
      "status": "running",
      "coordinator": "DocumentQueueProcessor",
      "startedAt": "2026-01-12T12:34:57Z"
    },
    {
      "id": "signal:content.text",
      "label": "content.text",
      "type": "signal",
      "emittedBy": "step:ContentExtraction",
      "kept": true
    },
    {
      "id": "signal:escalation.required",
      "label": "escalation.required",
      "type": "escalation",
      "emittedBy": "step:TableExtraction",
      "reason": "Complex table structure"
    }
  ],
  "edges": [
    {
      "id": "e1",
      "from": "step:ContentExtraction",
      "to": "signal:content.text",
      "type": "emits"
    },
    {
      "id": "e2",
      "from": "signal:content.text",
      "to": "step:TableExtraction",
      "type": "consumes"
    },
    {
      "id": "e3",
      "from": "step:TableExtraction",
      "to": "signal:escalation.required",
      "type": "emits"
    },
    {
      "id": "e4",
      "from": "signal:escalation.required",
      "to": "step:LLMTableAnalysis",
      "type": "triggers",
      "style": "dashed"
    }
  ],
  "metadata": {
    "runId": "abc123",
    "workflowKey": "document-pipeline",
    "documentId": "doc-789",
    "documentName": "annual_report.pdf",
    "totalDurationMs": 5678,
    "status": "running",
    "updatedAt": "2026-01-12T12:35:10Z"
  }
}
```

---

## SignalR Hub

### WorkflowHub

**File:** `src/LucidRAG/Hubs/WorkflowHub.cs`

```csharp
public interface IWorkflowHubClient
{
    Task OnWorkflowEvent(WorkflowEventDto @event);
    Task OnGraphUpdate(WorkflowGraphDto graph);
}

[Authorize]
public class WorkflowHub : Hub<IWorkflowHubClient>
{
    public async Task SubscribeToRun(Guid runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"run_{runId}");
    }

    public async Task UnsubscribeFromRun(Guid runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"run_{runId}");
    }

    public async Task SubscribeToDocument(Guid documentId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"document_{documentId}");
    }

    public async Task UnsubscribeFromDocument(Guid documentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"document_{documentId}");
    }
}
```

**Registration in `Program.cs`:**

```csharp
app.MapHub<WorkflowHub>("/hubs/workflow");
```

---

## UI Components

### Alpine.js State Extension

**File:** `src/LucidRAG/Views/Home/Index.cshtml` (add to `ragApp()`)

```javascript
// Workflow graph state
workflowGraph: {
    enabled: false,
    runId: null,
    documentId: null,
    data: null,           // { nodes: [], edges: [], metadata: {} }
    viewMode: 'merged',   // 'definition' | 'signals' | 'merged'
    selectedNode: null,
    loading: false,
    liveMode: false,
    hubConnection: null
},

// Methods
async loadWorkflowGraph(documentId) {
    this.workflowGraph.loading = true;
    this.workflowGraph.documentId = documentId;

    try {
        const response = await fetch(`/api/workflow/documents/${documentId}/graph?view=${this.workflowGraph.viewMode}`);
        if (response.ok) {
            this.workflowGraph.data = await response.json();
            this.workflowGraph.runId = this.workflowGraph.data.metadata.runId;
            this.workflowGraph.enabled = true;
            this.$nextTick(() => this.renderWorkflowGraph());
        }
    } finally {
        this.workflowGraph.loading = false;
    }
},

async subscribeToWorkflowUpdates() {
    if (this.workflowGraph.hubConnection) return;

    this.workflowGraph.hubConnection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/workflow')
        .withAutomaticReconnect()
        .build();

    this.workflowGraph.hubConnection.on('OnWorkflowEvent', (event) => {
        this.handleWorkflowEvent(event);
    });

    this.workflowGraph.hubConnection.on('OnGraphUpdate', (graph) => {
        this.workflowGraph.data = graph;
        this.renderWorkflowGraph();
    });

    await this.workflowGraph.hubConnection.start();

    if (this.workflowGraph.runId) {
        await this.workflowGraph.hubConnection.invoke('SubscribeToRun', this.workflowGraph.runId);
    }
},

handleWorkflowEvent(event) {
    // Incrementally update the graph data
    if (!this.workflowGraph.data) return;

    const nodeId = event.stepKey ? `step:${event.stepKey}` :
                   event.signalKey ? `signal:${event.signalKey}` : null;

    if (nodeId) {
        const existingNode = this.workflowGraph.data.nodes.find(n => n.id === nodeId);
        if (existingNode) {
            existingNode.status = event.status;
            existingNode.durationMs = event.durationMs;
        } else if (event.eventType.includes('started')) {
            // Add new node
            this.workflowGraph.data.nodes.push({
                id: nodeId,
                label: event.stepKey || event.signalKey,
                type: event.stepKey ? 'step' : 'signal',
                status: event.status,
                startedAt: event.timestamp
            });
        }
    }

    // Debounced re-render
    this.debouncedRenderWorkflowGraph();
},

debouncedRenderWorkflowGraph: null, // Initialize with debounce in init()

renderWorkflowGraph() {
    const container = this.$refs.workflowGraphCanvas;
    if (!container || !this.workflowGraph.data) return;

    const { nodes, edges } = this.workflowGraph.data;
    const width = container.clientWidth;
    const height = container.clientHeight || 500;

    // Clear previous
    d3.select(container).selectAll('*').remove();

    const svg = d3.select(container)
        .append('svg')
        .attr('width', width)
        .attr('height', height);

    // Create arrow marker for edges
    svg.append('defs').append('marker')
        .attr('id', 'arrowhead')
        .attr('viewBox', '-0 -5 10 10')
        .attr('refX', 20)
        .attr('refY', 0)
        .attr('orient', 'auto')
        .attr('markerWidth', 6)
        .attr('markerHeight', 6)
        .append('path')
        .attr('d', 'M 0,-5 L 10,0 L 0,5')
        .attr('fill', '#6b7280');

    // Build D3 force simulation
    const simulation = d3.forceSimulation(nodes)
        .force('link', d3.forceLink(edges)
            .id(d => d.id)
            .distance(100))
        .force('charge', d3.forceManyBody().strength(-300))
        .force('center', d3.forceCenter(width / 2, height / 2))
        .force('collision', d3.forceCollide().radius(40));

    // Draw edges
    const link = svg.append('g')
        .selectAll('line')
        .data(edges)
        .enter().append('line')
        .attr('stroke', d => d.type === 'triggers' ? '#f59e0b' : '#6b7280')
        .attr('stroke-width', 2)
        .attr('stroke-dasharray', d => d.style === 'dashed' ? '5,5' : null)
        .attr('marker-end', 'url(#arrowhead)');

    // Draw nodes
    const node = svg.append('g')
        .selectAll('g')
        .data(nodes)
        .enter().append('g')
        .attr('class', 'workflow-node')
        .call(d3.drag()
            .on('start', dragstarted)
            .on('drag', dragged)
            .on('end', dragended));

    // Node shapes based on type
    node.each(function(d) {
        const el = d3.select(this);
        if (d.type === 'step') {
            // Rounded rectangle for steps
            el.append('rect')
                .attr('width', 120)
                .attr('height', 40)
                .attr('x', -60)
                .attr('y', -20)
                .attr('rx', 8)
                .attr('fill', getStatusColor(d.status))
                .attr('stroke', getStatusBorder(d.status))
                .attr('stroke-width', 2);
        } else if (d.type === 'signal') {
            // Diamond for signals
            el.append('polygon')
                .attr('points', '0,-15 15,0 0,15 -15,0')
                .attr('fill', d.kept ? '#10b981' : '#6b7280')
                .attr('stroke', '#374151')
                .attr('stroke-width', 1);
        } else if (d.type === 'escalation') {
            // Triangle for escalation
            el.append('polygon')
                .attr('points', '0,-18 16,12 -16,12')
                .attr('fill', '#f59e0b')
                .attr('stroke', '#d97706')
                .attr('stroke-width', 2);
        }
    });

    // Node labels
    node.append('text')
        .attr('dy', d => d.type === 'step' ? 4 : 25)
        .attr('text-anchor', 'middle')
        .attr('font-size', '11px')
        .attr('fill', d => d.type === 'step' ? '#fff' : '#374151')
        .text(d => truncate(d.label, 15));

    // Click handler
    node.on('click', (event, d) => {
        this.workflowGraph.selectedNode = d;
    });

    // Simulation tick
    simulation.on('tick', () => {
        link
            .attr('x1', d => d.source.x)
            .attr('y1', d => d.source.y)
            .attr('x2', d => d.target.x)
            .attr('y2', d => d.target.y);

        node.attr('transform', d => `translate(${d.x},${d.y})`);
    });

    // Helper functions
    function getStatusColor(status) {
        switch (status) {
            case 'running': return '#3b82f6';
            case 'success': return '#10b981';
            case 'failed': return '#ef4444';
            case 'skipped': return '#6b7280';
            default: return '#9ca3af';
        }
    }

    function getStatusBorder(status) {
        switch (status) {
            case 'running': return '#1d4ed8';
            case 'success': return '#059669';
            case 'failed': return '#dc2626';
            default: return '#4b5563';
        }
    }

    function truncate(str, len) {
        return str.length > len ? str.substring(0, len) + '...' : str;
    }

    function dragstarted(event, d) {
        if (!event.active) simulation.alphaTarget(0.3).restart();
        d.fx = d.x;
        d.fy = d.y;
    }

    function dragged(event, d) {
        d.fx = event.x;
        d.fy = event.y;
    }

    function dragended(event, d) {
        if (!event.active) simulation.alphaTarget(0);
        d.fx = null;
        d.fy = null;
    }
}
```

### UI Template Addition

**Add to existing view modes in `Index.cshtml`:**

```html
<!-- Workflow Graph View Mode Tab -->
<button @click="viewMode = 'workflow'; loadWorkflowGraph(selectedDocuments[0]?.id)"
        :class="viewMode === 'workflow' ? 'tab-active' : ''"
        class="tab"
        x-show="selectedDocuments.length === 1">
    Workflow
</button>

<!-- Workflow Graph Panel -->
<div x-show="viewMode === 'workflow'" class="h-full flex flex-col">
    <!-- Toolbar -->
    <div class="flex items-center gap-2 p-2 border-b border-base-300 bg-base-200">
        <select x-model="workflowGraph.viewMode"
                @change="loadWorkflowGraph(workflowGraph.documentId)"
                class="select select-sm select-bordered">
            <option value="merged">Merged View</option>
            <option value="definition">Definition Only</option>
            <option value="signals">Signals Only</option>
        </select>

        <label class="label cursor-pointer gap-2">
            <input type="checkbox" x-model="workflowGraph.liveMode"
                   @change="workflowGraph.liveMode ? subscribeToWorkflowUpdates() : null"
                   class="checkbox checkbox-sm checkbox-primary" />
            <span class="label-text text-sm">Live Updates</span>
        </label>

        <div class="flex-1"></div>

        <span x-show="workflowGraph.data?.metadata" class="text-xs text-base-content/60">
            Run: <span x-text="workflowGraph.data?.metadata?.runId?.substring(0,8)"></span>
            <span x-show="workflowGraph.data?.metadata?.totalDurationMs">
                (<span x-text="(workflowGraph.data?.metadata?.totalDurationMs / 1000).toFixed(1)"></span>s)
            </span>
        </span>
    </div>

    <!-- Graph Canvas -->
    <div class="flex-1 relative">
        <div x-ref="workflowGraphCanvas" class="absolute inset-0"></div>

        <!-- Loading overlay -->
        <div x-show="workflowGraph.loading"
             class="absolute inset-0 flex items-center justify-center bg-base-100/80">
            <span class="loading loading-spinner loading-lg"></span>
        </div>

        <!-- Empty state -->
        <div x-show="!workflowGraph.loading && !workflowGraph.data"
             class="absolute inset-0 flex items-center justify-center text-base-content/50">
            <div class="text-center">
                <svg class="w-16 h-16 mx-auto mb-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5"
                          d="M9 17V7m0 10a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2h2a2 2 0 012 2m0 10a2 2 0 002 2h2a2 2 0 002-2M9 7a2 2 0 012-2h2a2 2 0 012 2m0 10V7m0 10a2 2 0 002 2h2a2 2 0 002-2V7a2 2 0 00-2-2h-2a2 2 0 00-2 2"/>
                </svg>
                <p>No workflow data available</p>
                <p class="text-sm">Process a document to see its workflow</p>
            </div>
        </div>
    </div>

    <!-- Node Details Sidebar -->
    <div x-show="workflowGraph.selectedNode"
         class="absolute right-0 top-0 bottom-0 w-72 bg-base-100 border-l border-base-300 p-4 overflow-y-auto shadow-lg">
        <div class="flex items-center justify-between mb-4">
            <h3 class="font-semibold" x-text="workflowGraph.selectedNode?.label"></h3>
            <button @click="workflowGraph.selectedNode = null" class="btn btn-ghost btn-xs btn-circle">
                <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
                </svg>
            </button>
        </div>

        <div class="space-y-3 text-sm">
            <div>
                <span class="text-base-content/60">Type:</span>
                <span class="badge badge-sm ml-2" x-text="workflowGraph.selectedNode?.type"></span>
            </div>
            <div>
                <span class="text-base-content/60">Status:</span>
                <span class="badge badge-sm ml-2"
                      :class="{
                          'badge-info': workflowGraph.selectedNode?.status === 'running',
                          'badge-success': workflowGraph.selectedNode?.status === 'success',
                          'badge-error': workflowGraph.selectedNode?.status === 'failed',
                          'badge-ghost': workflowGraph.selectedNode?.status === 'skipped'
                      }"
                      x-text="workflowGraph.selectedNode?.status"></span>
            </div>
            <div x-show="workflowGraph.selectedNode?.durationMs">
                <span class="text-base-content/60">Duration:</span>
                <span x-text="workflowGraph.selectedNode?.durationMs + 'ms'"></span>
            </div>
            <div x-show="workflowGraph.selectedNode?.coordinator">
                <span class="text-base-content/60">Coordinator:</span>
                <span x-text="workflowGraph.selectedNode?.coordinator"></span>
            </div>
            <div x-show="workflowGraph.selectedNode?.startedAt">
                <span class="text-base-content/60">Started:</span>
                <span x-text="new Date(workflowGraph.selectedNode?.startedAt).toLocaleTimeString()"></span>
            </div>
            <div x-show="workflowGraph.selectedNode?.reason">
                <span class="text-base-content/60">Reason:</span>
                <span x-text="workflowGraph.selectedNode?.reason"></span>
            </div>
        </div>
    </div>
</div>
```

### Visual Legend Component

```html
<!-- Add to workflow graph toolbar or as a collapsible panel -->
<div class="dropdown dropdown-end">
    <label tabindex="0" class="btn btn-ghost btn-xs">
        <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                  d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/>
        </svg>
        Legend
    </label>
    <div tabindex="0" class="dropdown-content z-50 card card-compact w-64 p-2 shadow bg-base-100 border border-base-300">
        <div class="card-body">
            <h4 class="font-semibold text-sm mb-2">Node Types</h4>
            <div class="space-y-1 text-xs">
                <div class="flex items-center gap-2">
                    <div class="w-6 h-4 rounded bg-blue-500"></div>
                    <span>Step (running)</span>
                </div>
                <div class="flex items-center gap-2">
                    <div class="w-6 h-4 rounded bg-green-500"></div>
                    <span>Step (success)</span>
                </div>
                <div class="flex items-center gap-2">
                    <div class="w-6 h-4 rounded bg-red-500"></div>
                    <span>Step (failed)</span>
                </div>
                <div class="flex items-center gap-2">
                    <div class="w-4 h-4 rotate-45 bg-gray-500"></div>
                    <span>Signal</span>
                </div>
                <div class="flex items-center gap-2">
                    <div class="w-0 h-0 border-l-[8px] border-r-[8px] border-b-[12px] border-l-transparent border-r-transparent border-b-amber-500"></div>
                    <span>Escalation</span>
                </div>
            </div>
            <h4 class="font-semibold text-sm mt-3 mb-2">Edge Types</h4>
            <div class="space-y-1 text-xs">
                <div class="flex items-center gap-2">
                    <div class="w-6 h-0.5 bg-gray-500"></div>
                    <span>Data flow</span>
                </div>
                <div class="flex items-center gap-2">
                    <div class="w-6 h-0.5 bg-amber-500 border-dashed border-t-2 border-amber-500"></div>
                    <span>Escalation trigger</span>
                </div>
            </div>
        </div>
    </div>
</div>
```

---

## Instrumentation Points

### DocumentProcessingService Integration

**File:** `src/LucidRAG/Services/DocumentProcessingService.cs`

Add instrumentation calls:

```csharp
public async Task ProcessDocumentAsync(Document document, CancellationToken ct)
{
    var runId = Guid.NewGuid();

    await _workflowEventSink.RunStartedAsync(runId, "document-pipeline",
        document.Id, document.CollectionId);

    var stopwatch = Stopwatch.StartNew();

    try
    {
        // Content extraction
        await _workflowEventSink.StepStartedAsync(runId, "ContentExtraction");
        var content = await ExtractContentAsync(document, ct);
        await _workflowEventSink.SignalEmittedAsync(runId, "content.text",
            new { length = content.Length, kept = true });
        await _workflowEventSink.StepCompletedAsync(runId, "ContentExtraction",
            (int)stepWatch.ElapsedMilliseconds);

        // Table extraction
        if (ShouldExtractTables(document))
        {
            await _workflowEventSink.StepStartedAsync(runId, "TableExtraction");
            var tables = await _tableService.ExtractTablesAsync(document, ct);
            await _workflowEventSink.SignalEmittedAsync(runId, "tables.extracted",
                new { count = tables.Count, kept = tables.Any() });
            await _workflowEventSink.StepCompletedAsync(runId, "TableExtraction",
                (int)stepWatch.ElapsedMilliseconds);
        }

        // Entity extraction with potential escalation
        await _workflowEventSink.StepStartedAsync(runId, "EntityExtraction");
        var (entities, escalated) = await ExtractEntitiesAsync(document, ct);
        if (escalated)
        {
            await _workflowEventSink.EscalationTriggeredAsync(runId, "EntityExtraction",
                "Complex entity relationships detected", "LLM-Enhanced");
        }
        await _workflowEventSink.SignalEmittedAsync(runId, "entities.extracted",
            new { count = entities.Count, kept = true });
        await _workflowEventSink.StepCompletedAsync(runId, "EntityExtraction",
            (int)stepWatch.ElapsedMilliseconds);

        // ... more steps

        await _workflowEventSink.RunCompletedAsync(runId, (int)stopwatch.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        await _workflowEventSink.RunFailedAsync(runId, ex.Message);
        throw;
    }
}
```

---

## Implementation Plan

### Phase 1: Data Model & Infrastructure (Foundation)

**Tasks:**
1. Create `WorkflowEvent` entity and enum in `src/LucidRAG/Data/Entities/`
2. Add DbSet to `ApplicationDbContext`
3. Create and run EF Core migration
4. Implement `IWorkflowEventSink` interface and `WorkflowEventSink` service
5. Register services in DI container
6. Add `WorkflowHub` SignalR hub

**Deliverables:**
- Database schema ready
- Event sink emitting to DB + SignalR
- Hub endpoint at `/hubs/workflow`

### Phase 2: Instrumentation

**Tasks:**
1. Add instrumentation to `DocumentProcessingService`
2. Add instrumentation to `TableProcessingService`
3. Add instrumentation to `GraphRagPipeline`
4. Add instrumentation to `DocumentQueueProcessor`
5. Define standard step keys and signal keys in constants

**Deliverables:**
- All document processing stages emit workflow events
- Events visible in database after processing a document

### Phase 3: API & Graph Builder

**Tasks:**
1. Create `WorkflowGraphBuilder` service
2. Create `WorkflowGraphController` with endpoints
3. Implement graph JSON schema DTOs
4. Add SSE streaming endpoint for live events
5. Add retention service as `IHostedService`

**Deliverables:**
- API returns graph JSON for any document
- Live event streaming works
- Old traces auto-cleaned

### Phase 4: UI Integration

**Tasks:**
1. Add workflow state to Alpine.js `ragApp()`
2. Add "Workflow" view mode tab
3. Implement D3.js graph rendering
4. Connect SignalR for live updates
5. Add node details sidebar
6. Add legend and toolbar controls

**Deliverables:**
- Users can click document → see workflow graph
- Live updates during processing
- Interactive node selection

### Phase 5: Polish & Optimization

**Tasks:**
1. Add filters (by status, step type)
2. Add export graph JSON button
3. Implement performance guards (max 500 nodes)
4. Add debounced re-layout for live mode
5. Add workflow comparison view (two runs side-by-side)
6. Add duration histograms and stats

**Deliverables:**
- Production-ready feature
- Performance optimized for large workflows

---

## File Structure Summary

```
src/LucidRAG/
├── Data/
│   ├── Entities/
│   │   └── WorkflowEvent.cs              # New entity
│   ├── Migrations/
│   │   └── YYYYMMDD_AddWorkflowEvents.cs # New migration
│   └── ApplicationDbContext.cs           # Add DbSet
├── Services/
│   └── Workflow/
│       ├── IWorkflowEventSink.cs         # Interface
│       ├── WorkflowEventSink.cs          # Implementation
│       ├── WorkflowGraphBuilder.cs       # Graph assembly
│       └── WorkflowEventRetentionService.cs # Background cleanup
├── Controllers/
│   └── Api/
│       └── WorkflowGraphController.cs    # REST API
├── Hubs/
│   └── WorkflowHub.cs                    # SignalR hub
├── Models/
│   └── Workflow/
│       ├── WorkflowGraphDto.cs           # Graph JSON model
│       ├── WorkflowEventDto.cs           # Event model
│       └── WorkflowRunSummary.cs         # Run list model
└── Views/
    └── Home/
        └── Index.cshtml                  # Add workflow view mode
```

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| High event volume | DB bloat, slow queries | Retention policy (4 traces), indexes, sampling |
| Layout jitter on live updates | Poor UX | Debounce re-layout (300ms), preserve positions |
| Ambiguous signal-to-step mapping | Incorrect edges | Include `source` in payload, infer when missing |
| Large graphs (500+ nodes) | Performance | Max node cap, collapse repeated signals |
| SignalR connection drops | Missed events | Auto-reconnect, fetch full graph on reconnect |

---

## Open Questions

1. **Scope**: Which workflows first? (document-pipeline is primary, image/audio later?)
2. **Trace mode**: Per-request debug flag or global config toggle?
3. **Retention**: 4 traces per document sufficient, or configurable per collection?
4. **Historical comparison**: Side-by-side run comparison worth building in Phase 5?
5. **Metrics dashboard**: Separate page for aggregate workflow analytics?

---

## Success Criteria

- [ ] User can click a processed document and see its workflow graph
- [ ] Graph shows all processing steps with correct status colors
- [ ] Live updates work during document processing
- [ ] Escalation paths are clearly visible
- [ ] Node click shows step details (duration, signals, errors)
- [ ] Old traces are automatically cleaned up
- [ ] Performance acceptable for documents with 50+ processing steps
