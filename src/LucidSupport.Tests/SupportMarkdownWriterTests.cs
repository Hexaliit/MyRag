using LucidSupport.Models;
using LucidSupport.Services.Ingestion;
using LucidSupport.Services.Learning;

namespace LucidSupport.Tests;

public class SupportMarkdownWriterTests
{
    [Fact]
    public void Write_MinimalModel_ProducesValidMarkdown()
    {
        var model = new PageModel
        {
            PageId = "test-page",
            UrlPattern = "/test*",
            Title = "Test Page"
        };

        var markdown = SupportMarkdownWriter.Write(model);

        Assert.Contains("page_id: test-page", markdown);
        Assert.Contains("url_pattern: /test*", markdown);
        Assert.Contains("title: Test Page", markdown);
        Assert.Contains("# Test Page", markdown);
    }

    [Fact]
    public void Write_WithFields_ProducesFieldSection()
    {
        var model = new PageModel
        {
            PageId = "form-page",
            UrlPattern = "/form*",
            Title = "Form",
            Fields =
            [
                new FieldDefinition
                {
                    Selector = "#email",
                    Label = "Email Address",
                    Type = "email",
                    Pattern = "email",
                    Required = true,
                    Help = "Enter your email.",
                    ClientValidation = ["required"],
                    Errors = new Dictionary<string, string>
                    {
                        ["required"] = "Email is required"
                    }
                }
            ]
        };

        var markdown = SupportMarkdownWriter.Write(model);

        Assert.Contains("## Fields", markdown);
        Assert.Contains("### [#email] Email Address", markdown);
        Assert.Contains("- type: email", markdown);
        Assert.Contains("- pattern: email", markdown);
        Assert.Contains("- required: true", markdown);
        Assert.Contains("- help: Enter your email.", markdown);
        Assert.Contains("- validation:", markdown);
        Assert.Contains("  - client: required", markdown);
        Assert.Contains("- errors:", markdown);
        Assert.Contains("  - required: \"Email is required\"", markdown);
    }

    [Fact]
    public void Write_WithConditions_ProducesBlockquotes()
    {
        var model = new PageModel
        {
            PageId = "cond-page",
            UrlPattern = "/cond*",
            Title = "Conditions",
            Conditions =
            [
                new ConditionRule
                {
                    When = "[#email].error",
                    Suggest = "Check your email address.",
                    Highlight = "#email"
                }
            ]
        };

        var markdown = SupportMarkdownWriter.Write(model);

        Assert.Contains("## Conditions", markdown);
        Assert.Contains("> when: [#email].error", markdown);
        Assert.Contains("> suggest: Check your email address.", markdown);
        Assert.Contains("> highlight: #email", markdown);
    }

    [Fact]
    public void Write_WithTopics_ProducesTopicsList()
    {
        var model = new PageModel
        {
            PageId = "topic-page",
            UrlPattern = "/topics*",
            Title = "Topics",
            Topics =
            [
                new TopicMapping
                {
                    Question = "How do I reset my password?",
                    ArticleId = "password-reset"
                }
            ]
        };

        var markdown = SupportMarkdownWriter.Write(model);

        Assert.Contains("## Topics", markdown);
        Assert.Contains("\"How do I reset my password?\"", markdown);
        Assert.Contains("password-reset", markdown);
    }

    [Fact]
    public void Write_OmitsEmptySections()
    {
        var model = new PageModel
        {
            PageId = "empty",
            UrlPattern = "/empty*",
            Title = "Empty"
        };

        var markdown = SupportMarkdownWriter.Write(model);

        Assert.DoesNotContain("## Fields", markdown);
        Assert.DoesNotContain("## Conditions", markdown);
        Assert.DoesNotContain("## Topics", markdown);
    }

    [Fact]
    public void Write_OmitsOptionalFrontmatterWhenNull()
    {
        var model = new PageModel
        {
            PageId = "minimal",
            UrlPattern = "/minimal*",
            Title = "Minimal"
        };

        var markdown = SupportMarkdownWriter.Write(model);

        Assert.DoesNotContain("flow:", markdown);
        Assert.DoesNotContain("step:", markdown);
        Assert.DoesNotContain("prev:", markdown);
        Assert.DoesNotContain("next:", markdown);
        Assert.DoesNotContain("site:", markdown);
    }

    [Fact]
    public void RoundTrip_FullModel_PreservesData()
    {
        var original = new PageModel
        {
            PageId = "checkout-payment",
            UrlPattern = "/checkout/payment*",
            Title = "Payment Page",
            Learned = new DateTimeOffset(2026, 2, 4, 12, 0, 0, TimeSpan.Zero),
            Site = "myapp.example.com",
            Flow = "checkout",
            Step = 2,
            Prev = "checkout-shipping",
            Description = "Collects payment details.",
            Layouts =
            [
                new LayoutBreakpoint { Name = "mobile", MaxWidth = 767, Columns = 1 },
                new LayoutBreakpoint { Name = "desktop", MinWidth = 1024, Columns = 2 }
            ],
            Nav = new Dictionary<string, NavInfo>
            {
                ["back"] = new() { Label = "Back", Selector = "#back-link" },
                ["next"] = new() { Label = "Pay", Selector = "#pay-btn" }
            },
            Fields =
            [
                new FieldDefinition
                {
                    Selector = "#card",
                    Label = "Card Number",
                    Type = "text",
                    Pattern = "credit-card",
                    Help = "Enter card number.",
                    ClientValidation = ["required"],
                    ServerValidation = ["Luhn check"],
                    Errors = new Dictionary<string, string>
                    {
                        ["required"] = "Required"
                    }
                }
            ],
            Conditions =
            [
                new ConditionRule
                {
                    When = "[#card].error",
                    Suggest = "Check your card.",
                    Highlight = "#card"
                }
            ],
            Topics =
            [
                new TopicMapping { Question = "Payment methods?", ArticleId = "methods" }
            ]
        };

        var markdown = SupportMarkdownWriter.Write(original);
        var parsed = SupportMarkdownParser.Parse(markdown);

        Assert.Equal(original.PageId, parsed.PageId);
        Assert.Equal(original.UrlPattern, parsed.UrlPattern);
        Assert.Equal(original.Title, parsed.Title);
        Assert.Equal(original.Site, parsed.Site);
        Assert.Equal(original.Flow, parsed.Flow);
        Assert.Equal(original.Step, parsed.Step);
        Assert.Equal(original.Prev, parsed.Prev);

        Assert.Equal(original.Fields.Count, parsed.Fields.Count);
        Assert.Equal(original.Fields[0].Selector, parsed.Fields[0].Selector);
        Assert.Equal(original.Fields[0].Pattern, parsed.Fields[0].Pattern);
        Assert.Equal(original.Fields[0].Help, parsed.Fields[0].Help);

        Assert.Equal(original.Conditions.Count, parsed.Conditions.Count);
        Assert.Equal(original.Conditions[0].When, parsed.Conditions[0].When);
        Assert.Equal(original.Conditions[0].Suggest, parsed.Conditions[0].Suggest);
        Assert.Equal(original.Conditions[0].Highlight, parsed.Conditions[0].Highlight);

        Assert.Equal(original.Topics.Count, parsed.Topics.Count);
        Assert.Equal(original.Topics[0].Question, parsed.Topics[0].Question);
        Assert.Equal(original.Topics[0].ArticleId, parsed.Topics[0].ArticleId);
    }
}
