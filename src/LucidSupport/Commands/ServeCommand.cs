using System.ComponentModel;
using LucidSupport.Endpoints;
using LucidSupport.Services.Ingestion;
using LucidSupport.Services.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LucidSupport.Commands;

/// <summary>
///     Exposes the resolved support directory path to endpoints that need to write new files.
/// </summary>
internal sealed record SupportConfig(string SupportDir);

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

        // Load all .support.md files
        var store = new PageModelStore();
        var supportFiles = Directory.GetFiles(supportDir, "*.support.md", SearchOption.AllDirectories);

        foreach (var file in supportFiles)
        {
            try
            {
                var model = SupportMarkdownParser.ParseFile(file);
                store.Add(model, file);
                AnsiConsole.MarkupLine($"  [green]✓[/] Loaded [cyan]{Path.GetFileName(file)}[/] → {model.PageId} ({model.Fields.Count} fields)");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠[/] Skipped [cyan]{Path.GetFileName(file)}[/]: {ex.Message}");
            }
        }

        if (store.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .support.md files loaded. The API will return 404 for all pages.[/]");
        }

        // Build web application
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register services
        builder.Services.AddSingleton(new SupportConfig(supportDir));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<TemplateResponseEngine>();
        builder.Services.AddSingleton<WorkflowEvaluator>();
        builder.Services.AddCors();

        // Configure camelCase JSON (matches TypeScript types exactly)
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();

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
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  Press [yellow]Ctrl+C[/] to stop.");
        AnsiConsole.WriteLine();

        await app.RunAsync($"http://localhost:{settings.Port}");
        return 0;
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
