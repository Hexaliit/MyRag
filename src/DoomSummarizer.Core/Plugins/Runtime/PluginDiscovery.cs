using System.Diagnostics;
using System.Reflection;

namespace DoomSummarizer.Plugins.Runtime;

/// <summary>
/// Scans assemblies for types implementing <see cref="ISourcePlugin"/> or <see cref="IOutputPlugin"/>.
/// </summary>
public static class PluginDiscovery
{
    /// <summary>
    /// Discover all <see cref="ISourcePlugin"/> implementations in a loaded assembly.
    /// </summary>
    public static IReadOnlyList<ISourcePlugin> DiscoverSourcePlugins(Assembly assembly)
    {
        var plugins = new List<ISourcePlugin>();

        try
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                if (!typeof(ISourcePlugin).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is ISourcePlugin plugin)
                        plugins.Add(plugin);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginDiscovery] Failed to create {type.FullName}: {ex.Message}");
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            Debug.WriteLine($"[PluginDiscovery] Type load errors in {assembly.GetName().Name}:");
            foreach (var le in ex.LoaderExceptions.Where(e => e != null))
                Debug.WriteLine($"  - {le!.Message}");
        }

        return plugins;
    }

    /// <summary>
    /// Discover all <see cref="IOutputPlugin"/> implementations in a loaded assembly.
    /// </summary>
    public static IReadOnlyList<IOutputPlugin> DiscoverOutputPlugins(Assembly assembly)
    {
        var plugins = new List<IOutputPlugin>();

        try
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                if (!typeof(IOutputPlugin).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is IOutputPlugin plugin)
                        plugins.Add(plugin);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginDiscovery] Failed to create {type.FullName}: {ex.Message}");
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            Debug.WriteLine($"[PluginDiscovery] Type load errors in {assembly.GetName().Name}:");
            foreach (var le in ex.LoaderExceptions.Where(e => e != null))
                Debug.WriteLine($"  - {le!.Message}");
        }

        return plugins;
    }

    /// <summary>
    /// Discover plugins from all assemblies in the current AppDomain.
    /// Used by DoomSummarizer.Complete to find statically-linked source packages.
    /// </summary>
    public static IReadOnlyList<ISourcePlugin> DiscoverAllSourcePlugins()
    {
        var plugins = new List<ISourcePlugin>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                plugins.AddRange(DiscoverSourcePlugins(assembly));
            }
            catch
            {
                // Skip assemblies that can't be scanned
            }
        }
        return plugins;
    }
}
