using DoomSummarizer.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizers.Reader.Docling;

/// <summary>
/// DI registration for the Docling reader.
/// </summary>
public static class DoclingReaderExtensions
{
    /// <summary>
    /// Register the Docling document reader.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="baseUrl">Docling service URL (default: http://localhost:5001).</param>
    public static IServiceCollection AddDoclingReader(this IServiceCollection services, string baseUrl = "http://localhost:5001")
    {
        services.AddHttpClient(nameof(DoclingReader));
        services.AddSingleton(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DoclingReader));
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DoclingReader>();
            return new DoclingReader(httpClient, logger, baseUrl);
        });
        services.AddSingleton<IDocumentReader>(sp => sp.GetRequiredService<DoclingReader>());
        return services;
    }
}
