using LucidRAG.LLM.Config;
using LucidRAG.LLM.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LucidRAG.LLM.Extensions;

/// <summary>
/// Extension methods for registering LucidRAG.LLM services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add unified LLM provider infrastructure with YAML configuration.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configDirectory">Directory containing llm-providers.yaml and prompts.yaml</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddLucidRagLlm(
        this IServiceCollection services,
        string configDirectory = "Config")
    {
        // Load LLM providers configuration from YAML
        services.AddSingleton<IOptions<LlmProviderConfig>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<LlmProviderConfig>>();
            var config = LoadYamlConfig<LlmProviderConfig>(configDirectory, "llm-providers.yaml", logger);
            return Options.Create(config ?? new LlmProviderConfig());
        });

        // Load prompts configuration from YAML
        services.AddSingleton<IOptions<PromptsConfig>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PromptsConfig>>();
            var config = LoadYamlConfig<PromptsConfig>(configDirectory, "prompts.yaml", logger);
            return Options.Create(config ?? new PromptsConfig());
        });

        // Register services
        RegisterCoreServices(services);

        return services;
    }

    /// <summary>
    /// Add unified LLM provider infrastructure with IConfiguration binding.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration root</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddLucidRagLlm(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind configuration from appsettings.json
        services.Configure<LlmProviderConfig>(configuration.GetSection(LlmProviderConfig.SectionName));
        services.Configure<PromptsConfig>(configuration.GetSection("Prompts"));

        // Register services
        RegisterCoreServices(services);

        return services;
    }

    /// <summary>
    /// Add unified LLM provider infrastructure with YAML configuration from specific paths.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="providersYamlPath">Path to llm-providers.yaml</param>
    /// <param name="promptsYamlPath">Path to prompts.yaml</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddLucidRagLlm(
        this IServiceCollection services,
        string providersYamlPath,
        string promptsYamlPath)
    {
        // Load LLM providers configuration from YAML
        services.AddSingleton<IOptions<LlmProviderConfig>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<LlmProviderConfig>>();
            var config = LoadYamlConfigFromPath<LlmProviderConfig>(providersYamlPath, logger);
            return Options.Create(config ?? new LlmProviderConfig());
        });

        // Load prompts configuration from YAML
        services.AddSingleton<IOptions<PromptsConfig>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PromptsConfig>>();
            var config = LoadYamlConfigFromPath<PromptsConfig>(promptsYamlPath, logger);
            return Options.Create(config ?? new PromptsConfig());
        });

        // Register services
        RegisterCoreServices(services);

        return services;
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        // Register prompt service
        services.AddSingleton<IPromptService, PromptService>();

        // Register provider factory
        services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();

        // Register backward-compatible ILlmService using default provider
        services.TryAddSingleton<ILlmService>(sp =>
        {
            var factory = sp.GetRequiredService<ILlmProviderFactory>();
            try
            {
                return factory.GetDefault();
            }
            catch (KeyNotFoundException)
            {
                // Fall back to NullLlmService if no providers available
                sp.GetRequiredService<ILogger<ILlmService>>()
                    .LogWarning("No default LLM provider available, using NullLlmService");
                return new NullLlmService();
            }
        });
    }

    private static T? LoadYamlConfig<T>(string directory, string filename, ILogger logger) where T : class
    {
        var paths = new[]
        {
            Path.Combine(directory, filename),
            Path.Combine(AppContext.BaseDirectory, directory, filename),
            Path.Combine(AppContext.BaseDirectory, filename),
            filename
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return LoadYamlConfigFromPath<T>(path, logger);
            }
        }

        logger.LogWarning("YAML config file '{Filename}' not found in any search path", filename);
        return null;
    }

    private static T? LoadYamlConfigFromPath<T>(string path, ILogger logger) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("YAML config file not found: {Path}", path);
                return null;
            }

            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var config = deserializer.Deserialize<T>(yaml);
            logger.LogInformation("Loaded {Type} from {Path}", typeof(T).Name, path);
            return config;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load YAML config from {Path}", path);
            return null;
        }
    }
}
