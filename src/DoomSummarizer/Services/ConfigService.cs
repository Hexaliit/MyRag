using System.Reflection;
using System.Text.Json;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

public static class ConfigService
{
    public static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".doomsummarizer");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public static string GetConfigDir() => ConfigDir;

    public static async Task<DoomConfig> LoadAsync()
    {
        // Try user config first
        if (File.Exists(ConfigPath))
        {
            var json = await File.ReadAllTextAsync(ConfigPath);
            var config = JsonSerializer.Deserialize(json, DoomConfigContext.Default.DoomConfig);
            return config ?? await LoadDefaultAsync();
        }

        // Try local config
        var localConfig = Path.Combine(Directory.GetCurrentDirectory(), "doomsummarizer.json");
        if (File.Exists(localConfig))
        {
            var json = await File.ReadAllTextAsync(localConfig);
            var config = JsonSerializer.Deserialize(json, DoomConfigContext.Default.DoomConfig);
            return config ?? await LoadDefaultAsync();
        }

        return await LoadDefaultAsync();
    }

    private static async Task<DoomConfig> LoadDefaultAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "DoomSummarizer.Resources.default-config.json";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: Could not load default config, using hardcoded defaults[/]");
            return new DoomConfig();
        }

        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize(json, DoomConfigContext.Default.DoomConfig) ?? new DoomConfig();
    }

    public static async Task SaveAsync(DoomConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, DoomConfigContext.Default.DoomConfig);
        await File.WriteAllTextAsync(ConfigPath, json);
    }

    public static async Task InitializeDefaultConfigAsync()
    {
        Directory.CreateDirectory(ConfigDir);
        var config = await LoadDefaultAsync();
        await SaveAsync(config);
        AnsiConsole.MarkupLine($"[green]Created config at:[/] {ConfigPath}");
    }

    public static string GetDbPath(DoomConfig config)
    {
        var path = config.Storage.DbPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return path;
    }

    public static string GetVectorDbPath()
    {
        var path = Path.Combine(ConfigDir, "vectors.duckdb");
        Directory.CreateDirectory(ConfigDir);
        return path;
    }
}
