using DomainClassifier.Core.Extensions;
using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Models;
using DomainClassifier.Core.Services;
using DomainClassifier.Financial.Extensions;
using DomainClassifier.Narrative.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Tests;

public class DomainPluginRegistryTests
{
    [Fact]
    public void NoPluginsRegistered_GetAll_ReturnsEmpty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task NoPluginsRegistered_EnrichAsync_ReturnsEmpty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "test_1",
                Text = "Some financial text about earnings",
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.pdf"
            }
        };

        var result = await registry.EnrichAsync(chunks);

        Assert.Same(DomainEnrichmentResult.Empty, result);
    }

    [Fact]
    public void WithFinancialPlugin_GetAll_ReturnsPlugin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainFinancial();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        Assert.Single(registry.GetAll());
        Assert.Equal("financial", registry.GetAll()[0].DomainId);
    }

    [Fact]
    public void WithFinancialPlugin_GetById_ReturnsPlugin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainFinancial();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        var plugin = registry.GetById("financial");
        Assert.NotNull(plugin);
        Assert.Equal("Financial Domain", plugin.Name);
    }

    [Fact]
    public void WithFinancialPlugin_GetById_CaseInsensitive()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainFinancial();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        Assert.NotNull(registry.GetById("Financial"));
        Assert.NotNull(registry.GetById("FINANCIAL"));
    }

    [Fact]
    public void BothPluginsRegistered_GetAll_ReturnsBoth()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainFinancial();
        services.AddDomainNarrative();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        Assert.Equal(2, registry.GetAll().Count);
        Assert.NotNull(registry.GetById("financial"));
        Assert.NotNull(registry.GetById("narrative"));
    }

    [Fact]
    public async Task BothPlugins_FinancialTextSelectsFinancial()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainFinancial();
        services.AddDomainNarrative();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        var text = "The company reported quarterly earnings of $2.5B with revenue growth of 15%. " +
                   "EBITDA margins improved and the balance sheet remains strong. " +
                   "The board approved a dividend increase and the stock price surged.";

        var classifications = await registry.ClassifyAsync(text);
        var top = classifications.FirstOrDefault();

        Assert.NotNull(top);
        Assert.Equal("financial", top.DomainId);
    }

    [Fact]
    public async Task BothPlugins_NarrativeTextSelectsNarrative()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainFinancial();
        services.AddDomainNarrative();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        var text = """
                   Chapter 1

                   "My dear Mr. Bennet," said his lady, "have you heard that Netherfield Park is let at last?"

                   The protagonist of this novel had long been the narrator of her own destiny.
                   The dialogue flowed freely as the characters discussed matters of the heart.
                   """;

        var classifications = await registry.ClassifyAsync(text);
        var top = classifications.FirstOrDefault();

        Assert.NotNull(top);
        Assert.Equal("narrative", top.DomainId);
    }

    [Fact]
    public async Task DomainHint_SkipsClassification_UsesHintedPlugin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDomainFinancial();
        services.AddDomainNarrative();
        services.AddDomainClassifierRegistry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IDomainPluginRegistry>();

        // Financial text but with narrative hint
        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "test_1",
                Text = "The company reported quarterly earnings. Stock prices rose.",
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.txt"
            }
        };

        var result = await registry.EnrichAsync(chunks, domainHint: "narrative");

        Assert.NotEqual(DomainEnrichmentResult.Empty, result);
        Assert.True(result.Classifications.Count > 0);
        Assert.Equal("narrative", result.Classifications[0].DomainId);
        Assert.Equal(1.0, result.Classifications[0].Confidence);
    }
}
