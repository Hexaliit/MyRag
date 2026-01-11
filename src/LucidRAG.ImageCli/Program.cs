using System.CommandLine;
using System.Reflection;
using LucidRAG.ImageCli.Commands;
using LucidRAG.ImageCli.Services;
using LucidRAG.ImageCli.Services.OutputFormatters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using Serilog;
using Serilog.Events;
using Spectre.Console;

namespace LucidRAG.ImageCli;

internal class Program
{
    private static int Main(string[] args)
    {
        // Display banner
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h")) ShowBanner();

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", true)
            .AddUserSecrets<Program>(true) // Load API keys from user secrets
            .AddEnvironmentVariables("LUCIDRAG_")
            .Build();

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            // Build root command
            var rootCommand = new RootCommand("LucidRAG Image CLI - Advanced image analysis and processing");

            // Global options
            var verboseOption = new Option<bool>("--verbose", "-v")
                { Description = "Enable verbose logging", DefaultValueFactory = _ => false };
            var ollamaUrlOption = new Option<string?>("--ollama-url")
            {
                Description = "Ollama API base URL",
                DefaultValueFactory = _ => configuration["Ollama:BaseUrl"] ?? "http://localhost:11434"
            };

            rootCommand.Options.Add(verboseOption);
            rootCommand.Options.Add(ollamaUrlOption);

            // Add subcommands
            rootCommand.Subcommands.Add(AnalyzeCommand.Create());
            rootCommand.Subcommands.Add(BatchCommand.Create());
            rootCommand.Subcommands.Add(DedupeCommand.Create());
            rootCommand.Subcommands.Add(ExtractFramesCommand.Create());
            rootCommand.Subcommands.Add(PreviewCommand.Create());
            rootCommand.Subcommands.Add(ScoreCommand.Create());

            // Parse and execute
            return rootCommand.Parse(args).Invoke();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            AnsiConsole.MarkupLine($"[red]✗ Fatal error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ShowBanner()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

        AnsiConsole.Write(
            new FigletText("LucidRAG Image")
                .LeftJustified()
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine($"[dim]Version {version}[/]");
        AnsiConsole.MarkupLine("[dim]Advanced image analysis powered by DocSummarizer.Images[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    ///     Build a service provider with all required services.
    /// </summary>
    public static IServiceProvider BuildServiceProvider(IConfiguration configuration, bool verbose = false)
    {
        var services = new ServiceCollection();

        // Add configuration
        services.AddSingleton(configuration);

        // Add logging
        var logLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;
        services.AddLogging(builder =>
        {
            builder.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .WriteTo.Console()
                .CreateLogger());
        });

        // Add DocSummarizer.Images services
        services.AddDocSummarizerImages(configuration.GetSection("Images"));

        // Add Signal Database for caching (stores in user's app data directory)
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LucidRAG",
            "ImageCache");
        Directory.CreateDirectory(appDataPath);

        var dbPath = Path.Combine(appDataPath, "imageanalysis.db");
        services.AddSingleton<ISignalDatabase>(sp => new SignalDatabase(dbPath));

        // Add CLI-specific services
        services.AddSingleton<TableFormatter>();
        services.AddSingleton<JsonFormatter>();
        services.AddSingleton<MarkdownFormatter>();

        // Add vision LLM services (from core library)
        services.AddSingleton<VisionLlmService>();
        services.AddSingleton<UnifiedVisionService>();

        // Add escalation service (from core library)
        services.AddSingleton<EscalationService>();

        // Add batch processor (CLI-specific)
        services.AddSingleton<ImageBatchProcessor>();

        // Add deduplication service (from core library)
        services.AddSingleton<DeduplicationService>();

        return services.BuildServiceProvider();
    }
}