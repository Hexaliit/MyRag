using LucidRAG.Data;
using LucidRAG.UltraResearch;
using LucidRAG.UltraResearch.Tools;
using LucidResearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP server mode: lucidresearch --mcp
if (args.Length > 0 && args[0] is "--mcp" or "-mcp")
{
    var builder = Host.CreateApplicationBuilder(args.Skip(1).ToArray());

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    ServiceRegistration.ConfigureServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(UltraResearchTools).Assembly);

    var mcpApp = builder.Build();

    // Ensure database tables exist
    await using (var scope = mcpApp.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    // Configure UltraResearchTools with the orchestrator
    var orchestrator = mcpApp.Services.GetRequiredService<UltraResearchOrchestrator>();
    UltraResearchTools.Configure(orchestrator, mcpApp.Services);

    await mcpApp.RunAsync();
    return;
}

// Fullscreen TUI mode (default)
var services = ServiceRegistration.BuildServiceProvider(args);

// Ensure database tables exist
await using (var scope = services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await ResearchApp.RunAsync(args, services);
