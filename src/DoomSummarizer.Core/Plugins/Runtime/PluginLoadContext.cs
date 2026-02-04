using System.Reflection;
using System.Runtime.Loader;

namespace DoomSummarizer.Plugins.Runtime;

/// <summary>
///     Isolated assembly load context for runtime-loaded plugins.
///     Collectible so plugins can be unloaded if needed.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginDllPath)
        : base(true)
    {
        _resolver = new AssemblyDependencyResolver(pluginDllPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Try to resolve from the plugin's dependency graph first
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null) return LoadFromAssemblyPath(assemblyPath);

        // Fall back to default context (shared framework assemblies)
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null) return LoadUnmanagedDllFromPath(libraryPath);

        return IntPtr.Zero;
    }
}