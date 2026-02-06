using System.ComponentModel;
using LucidSupport.Endpoints;
using LucidSupport.Extensions;
using LucidSupport.Models;
using LucidSupport.Services.Ingestion;
using LucidSupport.Services.Knowledge;
using LucidSupport.Services.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LucidSupport.Commands;

internal sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port")]
        [Description("Port to listen on")]
        [DefaultValue(5050)]
        public int Port { get; set; } = 5050;

        [CommandOption("-d|--support-dir")]
        [Description("Directory containing .support.md files")]
        [DefaultValue("support-files")]
        public string SupportDir { get; set; } = "support-files";

        [CommandOption("--ai")]
        [Description("Enable AI-powered feedback via Ollama")]
        [DefaultValue(false)]
        public bool EnableAi { get; set; }

        [CommandOption("--ollama-url")]
        [Description("Ollama API base URL")]
        [DefaultValue("http://localhost:11434")]
        public string OllamaUrl { get; set; } = "http://localhost:11434";

        [CommandOption("--manual-dir")]
        [Description("Directory containing manual/knowledge files")]
        public string? ManualDir { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        // Resolve support directory (check relative to AppContext.BaseDirectory for published builds)
        var supportDir = ResolvePath(settings.SupportDir);
        if (!Directory.Exists(supportDir))
        {
            AnsiConsole.MarkupLine($"[red]Support directory not found:[/] {supportDir}");
            return 1;
        }

        // Resolve wwwroot directory
        var wwwrootDir = ResolvePath("wwwroot");
        if (!Directory.Exists(wwwrootDir))
        {
            AnsiConsole.MarkupLine($"[red]wwwroot directory not found:[/] {wwwrootDir}");
            return 1;
        }

        // Build configuration
        var config = new LucidSupportConfig
        {
            SupportDirectory = supportDir,
            OllamaBaseUrl = settings.OllamaUrl,
            EnableAiFeedback = settings.EnableAi,
            ManualDirectory = settings.ManualDir is not null ? ResolvePath(settings.ManualDir) : null
        };

        // Build web application
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register core services via extension methods
        builder.Services.AddLucidSupportRuntime(config);
        builder.Services.AddEscalationPlugins();
        builder.Services.AddCors();

        // Register manual knowledge if directory specified
        if (config.ManualDirectory is not null && Directory.Exists(config.ManualDirectory))
        {
            builder.Services.AddManualKnowledge(config.ManualDirectory);
        }

        // Register AI feedback if enabled
        if (config.EnableAiFeedback)
        {
            builder.Services.AddAiFeedback(config);
        }

        // Configure camelCase JSON (matches TypeScript types exactly)
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();

        // Load .support.md files into the store
        var store = app.Services.GetRequiredService<IPageModelStore>();
        LoadSupportFiles(store, supportDir);

        // Load knowledge files if manual directory specified
        if (config.ManualDirectory is not null && Directory.Exists(config.ManualDirectory))
        {
            var knowledgeStore = app.Services.GetService<ManualKnowledgeStore>();
            if (knowledgeStore is not null)
                LoadKnowledgeFiles(knowledgeStore, config.ManualDirectory);
        }

        // CORS (allow all for demo)
        app.UseCors(policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        // Serve static files from wwwroot
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwrootDir),
            ServeUnknownFileTypes = false
        });

        // Map API endpoints
        app.MapSupportEndpoints();
        app.MapAdminEndpoints();

        // Startup message
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]LucidSupport Demo Server[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [green]►[/] Listening on [link]http://localhost:{settings.Port}[/]");
        AnsiConsole.MarkupLine($"  [green]►[/] Admin: [link]http://localhost:{settings.Port}/admin/index.html[/]");
        AnsiConsole.MarkupLine($"  [green]►[/] Demo:  [link]http://localhost:{settings.Port}/demo/contact.html[/]");
        AnsiConsole.MarkupLine($"  [green]►[/] API:   [cyan]GET  /api/support/page?url=...[/]");
        AnsiConsole.MarkupLine($"  [green]►[/] API:   [cyan]POST /api/help/contextual[/]");
        AnsiConsole.MarkupLine($"  [green]►[/] Admin: [cyan]GET  /api/admin/pages[/]");
        AnsiConsole.MarkupLine($"  [green]►[/] Loaded [yellow]{store.Count}[/] support models from [cyan]{supportDir}[/]");

        if (config.ManualDirectory is not null)
            AnsiConsole.MarkupLine($"  [green]►[/] Knowledge: [cyan]{config.ManualDirectory}[/]");

        if (config.EnableAiFeedback)
            AnsiConsole.MarkupLine($"  [green]►[/] AI Feedback: [cyan]{config.OllamaBaseUrl}[/] ({config.SentinelModel})");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  Press [yellow]Ctrl+C[/] to stop.");
        AnsiConsole.WriteLine();

        await app.RunAsync($"http://localhost:{settings.Port}");
        return 0;
    }

    private static void LoadSupportFiles(IPageModelStore store, string supportDir)
    {
        var supportFiles = Directory.GetFiles(supportDir, "*.support.md", SearchOption.AllDirectories);

        foreach (var file in supportFiles)
        {
            try
            {
                var model = SupportMarkdownParser.ParseFile(file);
                store.Add(model, file);
                AnsiConsole.MarkupLine(
                    $"  [green]✓[/] Loaded [cyan]{Path.GetFileName(file)}[/] → {model.PageId} ({model.Fields.Count} fields)");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]⚠[/] Skipped [cyan]{Path.GetFileName(file)}[/]: {ex.Message}");
            }
        }

        if (store.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No .support.md files loaded. The API will return 404 for all pages.[/]");
        }
    }

    private static void LoadKnowledgeFiles(ManualKnowledgeStore knowledgeStore, string manualDir)
    {
        var knowledgeFiles = Directory.GetFiles(manualDir, "*.knowledge.json", SearchOption.AllDirectories);

        foreach (var file in knowledgeFiles)
        {
            try
            {
                knowledgeStore.LoadFromFile(file);
                AnsiConsole.MarkupLine(
                    $"  [green]✓[/] Knowledge: [cyan]{Path.GetFileName(file)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]⚠[/] Skipped knowledge [cyan]{Path.GetFileName(file)}[/]: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Resolve a path relative to the current directory or AppContext.BaseDirectory.
    ///     This ensures files are found in both dev (dotnet run) and published (single-file) scenarios.
    /// </summary>
    private static string ResolvePath(string relativePath)
    {
        // First try relative to current directory
        var path = Path.GetFullPath(relativePath);
        if (Directory.Exists(path) || File.Exists(path))
            return path;

        // Then try relative to the assembly location (for published builds)
        var basePath = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (Directory.Exists(basePath) || File.Exists(basePath))
            return basePath;

        return path; // Return first attempt for error messaging
    }
}
