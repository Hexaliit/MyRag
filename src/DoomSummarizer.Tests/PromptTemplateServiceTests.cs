using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

public class PromptTemplateServiceTests
{
    [Fact]
    public void LoadTemplate_EmbeddedResource_ReturnsContent()
    {
        // Act
        var content = PromptTemplateService.LoadTemplate("roundup");

        // Assert
        Assert.NotNull(content);
        Assert.Contains("EVIDENCE", content);
        Assert.Contains("{{TODAY}}", content);
    }

    [Fact]
    public void LoadTemplate_DashedName_ReturnsContent()
    {
        // Verify hyphenated filenames work as embedded resources
        var content = PromptTemplateService.LoadTemplate("ask-answer");

        Assert.NotNull(content);
        Assert.Contains("QUESTION", content);
    }

    [Fact]
    public void LoadTemplate_AllTemplatesExist()
    {
        var templates = new[]
        {
            "roundup", "answer", "digest", "ask-answer",
            "newsletter", "processed-digest",
            "blog-outline", "blog-intro", "blog-section", "blog-conclusion",
            "longform-outline", "longform-section"
        };

        foreach (var name in templates)
        {
            var content = PromptTemplateService.LoadTemplate(name);
            Assert.NotNull(content);
            Assert.True(content.Length > 0, $"Template '{name}' is empty");
        }
    }

    [Fact]
    public void LoadTemplate_Unknown_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            PromptTemplateService.LoadTemplate("nonexistent-template-xyz"));
    }

    [Fact]
    public void Render_SubstitutesVariables()
    {
        PromptTemplateService.ClearCache();

        var result = PromptTemplateService.Render("roundup", new Dictionary<string, object?>
        {
            ["VIBE_PROMPT"] = "Be snarky",
            ["USER_QUERY"] = "AI news",
            ["TODAY"] = "January 27, 2026",
            ["EVIDENCE"] = "[E1] ### Test Article\nURL: https://example.com"
        });

        Assert.Contains("Be snarky", result);
        Assert.Contains("AI news", result);
        Assert.Contains("January 27, 2026", result);
        Assert.Contains("[E1] ### Test Article", result);
    }

    [Fact]
    public void Render_MissingVariable_DoesNotCrash()
    {
        PromptTemplateService.ClearCache();

        // Render with only some variables — missing ones should become empty
        var result = PromptTemplateService.Render("roundup", new Dictionary<string, object?>
        {
            ["TODAY"] = "January 27, 2026",
            ["EVIDENCE"] = "some evidence"
        });

        Assert.NotNull(result);
        Assert.Contains("January 27, 2026", result);
    }

    [Fact]
    public void Render_DigestConditional_UserQueryPresent_IncludesTopicFilter()
    {
        PromptTemplateService.ClearCache();

        var result = PromptTemplateService.Render("digest", new Dictionary<string, object?>
        {
            ["TODAY"] = "January 27, 2026",
            ["VIBE"] = "neutral",
            ["VIBE_PROMPT"] = "Objective, balanced.",
            ["ITEMS"] = "## TECH\n- item1",
            ["USER_QUERY"] = ".NET news"
        });

        Assert.Contains("TOPIC: .NET news", result);
        Assert.Contains("only include relevant items", result);
    }

    [Fact]
    public void Render_DigestConditional_UserQueryEmpty_ExcludesTopicFilter()
    {
        PromptTemplateService.ClearCache();

        var result = PromptTemplateService.Render("digest", new Dictionary<string, object?>
        {
            ["TODAY"] = "January 27, 2026",
            ["VIBE"] = "neutral",
            ["VIBE_PROMPT"] = "Objective, balanced.",
            ["ITEMS"] = "## TECH\n- item1",
            ["USER_QUERY"] = ""
        });

        Assert.DoesNotContain("TOPIC:", result);
    }

    [Fact]
    public void Render_DigestConditional_UserQueryMissing_ExcludesTopicFilter()
    {
        PromptTemplateService.ClearCache();

        // USER_QUERY variable not provided at all
        var result = PromptTemplateService.Render("digest", new Dictionary<string, object?>
        {
            ["TODAY"] = "January 27, 2026",
            ["VIBE"] = "neutral",
            ["VIBE_PROMPT"] = "Objective, balanced.",
            ["ITEMS"] = "## TECH\n- item1"
        });

        Assert.DoesNotContain("TOPIC:", result);
    }

    [Fact]
    public void Render_BlogOutline_RawTag_PreservesJsonBraces()
    {
        PromptTemplateService.ClearCache();

        var result = PromptTemplateService.Render("blog-outline", new Dictionary<string, object?>
        {
            ["QUERY"] = "AI trends",
            ["EVIDENCE"] = "evidence text",
            ["OUTLINE_INSTRUCTIONS"] = "Create an outline"
        });

        // The {% raw %} block should produce literal JSON braces
        Assert.Contains("\"title\":", result);
        Assert.Contains("\"sections\":", result);
    }

    [Fact]
    public void Render_NewsletterTemplate_IncludesTopicFilterRule()
    {
        PromptTemplateService.ClearCache();

        var result = PromptTemplateService.Render("newsletter", new Dictionary<string, object?>
        {
            ["TODAY"] = "January 27, 2026",
            ["TOPIC_DESC"] = " about \".NET news\"",
            ["VIBE_PROMPT"] = "neutral",
            ["TOP_PICKS_EVIDENCE"] = "pick1",
            ["QUICK_HITS"] = "hit1"
        });

        Assert.Contains("SKIP any item whose title or content is NOT relevant", result);
        Assert.Contains(".NET news", result);
    }
}
