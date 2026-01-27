using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;
using Fluid;
using Fluid.Values;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
/// Template rendering service using Liquid templates (Fluid engine).
/// Supports console, file (markdown/html), and email formats.
/// </summary>
public class TemplateService
{
    private readonly FluidParser _parser;
    private readonly Dictionary<string, IFluidTemplate> _compiledTemplates = new();
    private readonly Dictionary<string, string> _templateSources;
    private readonly Dictionary<string, TemplateDefinition> _definitions = new();
    private readonly TemplateOptions _options;
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public TemplateService()
    {
        _parser = new FluidParser();
        _templateSources = GetBuiltInTemplates();
        _options = new TemplateOptions();

        // Register custom filters
        _options.Filters.AddFilter("truncate_words", TruncateWordsFilter);
        _options.Filters.AddFilter("sentiment_emoji", SentimentEmojiFilter);
        _options.Filters.AddFilter("sentiment_label", SentimentLabelFilter);
        _options.Filters.AddFilter("title_case", TitleCaseFilter);
        _options.Filters.AddFilter("strip_html", StripHtmlFilter);

        // Pre-compile built-in templates
        foreach (var (name, source) in _templateSources)
        {
            if (_parser.TryParse(source, out var template, out var error))
            {
                _compiledTemplates[name] = template;
            }
        }
    }

    /// <summary>
    /// Load custom templates from config directory.
    /// Supports .liquid files (Liquid rendering templates) and
    /// .yaml/.yml files (YAML template definitions that control LLM structure + rendering).
    /// </summary>
    public async Task LoadCustomTemplatesAsync(string templatesDir)
    {
        if (!Directory.Exists(templatesDir)) return;

        // Load Liquid templates
        foreach (var file in Directory.GetFiles(templatesDir, "*.liquid"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var source = await File.ReadAllTextAsync(file);

                if (_parser.TryParse(source, out var template, out var error))
                {
                    _compiledTemplates[name] = template;
                    _templateSources[name] = source;
                }
            }
            catch
            {
                // Skip invalid templates
            }
        }

        // Load YAML template definitions
        foreach (var file in Directory.EnumerateFiles(templatesDir, "*.yaml")
                     .Concat(Directory.EnumerateFiles(templatesDir, "*.yml")))
        {
            try
            {
                var yaml = await File.ReadAllTextAsync(file);
                var def = YamlDeserializer.Deserialize<TemplateDefinition>(yaml);
                if (def == null) continue;

                // Use filename as name if not specified in YAML
                if (string.IsNullOrEmpty(def.Name))
                    def.Name = Path.GetFileNameWithoutExtension(file);

                _definitions[def.Name] = def;

                // If the definition includes a custom Liquid template, compile and register it
                if (!string.IsNullOrEmpty(def.Template))
                {
                    if (_parser.TryParse(def.Template, out var compiled, out _))
                    {
                        _compiledTemplates[def.Name] = compiled;
                        _templateSources[def.Name] = def.Template;
                    }
                }
            }
            catch
            {
                // Skip invalid definitions
            }
        }
    }

    public IEnumerable<string> ListTemplates() => _compiledTemplates.Keys;

    /// <summary>List all loaded YAML template definitions.</summary>
    public IEnumerable<string> ListDefinitions() => _definitions.Keys;

    /// <summary>
    /// Get a template definition by name.
    /// Returns null if no YAML definition exists for this template.
    /// </summary>
    public TemplateDefinition? GetDefinition(string name) =>
        _definitions.GetValueOrDefault(name);

    public string? GetTemplateSource(string name) =>
        _templateSources.GetValueOrDefault(name);

    /// <summary>
    /// Render digest using specified template.
    /// </summary>
    public string Render(DigestData data, string templateName = "default")
    {
        if (!_compiledTemplates.TryGetValue(templateName, out var template))
        {
            template = _compiledTemplates["default"];
        }

        var context = new TemplateContext(data, _options);
        return template.Render(context);
    }

    /// <summary>
    /// Render from a template string directly.
    /// </summary>
    public string RenderFromString(string templateSource, DigestData data)
    {
        if (_parser.TryParse(templateSource, out var template, out var error))
        {
            var context = new TemplateContext(data, _options);
            return template.Render(context);
        }

        return $"Template error: {error}";
    }

    #region Custom Filters

    private static ValueTask<FluidValue> TruncateWordsFilter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var text = input.ToStringValue();
        var wordCount = (int)(args.At(0).ToNumberValue());
        var suffix = args.At(1).ToStringValue() ?? "...";

        if (string.IsNullOrEmpty(text)) return StringValue.Empty;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= wordCount) return new StringValue(text);

        return new StringValue(string.Join(" ", words.Take(wordCount)) + suffix);
    }

    private static ValueTask<FluidValue> SentimentEmojiFilter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var sentiment = (float)input.ToNumberValue();
        var emoji = sentiment switch
        {
            > 0.5f => "\ud83d\udfe2",  // Green
            > 0.1f => "\ud83d\udd35",  // Blue
            < -0.5f => "\ud83d\udd34", // Red
            < -0.1f => "\ud83d\udfe0", // Orange
            _ => "\u26aa"              // White
        };
        return new StringValue(emoji);
    }

    private static ValueTask<FluidValue> SentimentLabelFilter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var sentiment = (float)input.ToNumberValue();
        var label = sentiment switch
        {
            > 0.5f => "positive",
            > 0.1f => "slightly positive",
            < -0.5f => "negative",
            < -0.1f => "slightly negative",
            _ => "neutral"
        };
        return new StringValue(label);
    }

    private static ValueTask<FluidValue> TitleCaseFilter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var text = input.ToStringValue();
        if (string.IsNullOrEmpty(text)) return StringValue.Empty;
        return new StringValue(char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant());
    }

    private static ValueTask<FluidValue> StripHtmlFilter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var text = input.ToStringValue();
        if (string.IsNullOrEmpty(text)) return StringValue.Empty;
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        return new StringValue(stripped);
    }

    #endregion

    private static Dictionary<string, string> GetBuiltInTemplates() => new()
    {
        ["default"] = """
            # Doom-Scroll Digest - {{ date | date: "%B %d, %Y" }}

            {% if overview %}
            {{ overview }}

            {% endif %}
            {% for topic in items_by_topic %}
            ## {{ topic.name | upcase }}
            {% for item in topic.items limit: 5 %}
            - [{{ item.title }}]({{ item.url }})
            {% if item.summary %}  > {{ item.summary | truncate_words: 30 }}{% endif %}
            {% endfor %}

            {% endfor %}
            {% if what_to_watch %}
            ## What to Watch
            {{ what_to_watch }}

            {% endif %}
            ---
            *Generated by DoomSummarizer | {{ vibe }} vibe | {{ item_count }} items*
            """,

        ["console"] = """
            # {{ vibe | upcase }} DIGEST - {{ date | date: "%B %d, %Y" }}
            {% for topic in items_by_topic %}

            ## {{ topic.name | upcase }}
            {% for item in topic.items limit: 3 %}
            - {{ item.title }}
            {% if item.summary %}  {{ item.summary | truncate_words: 15 }}{% endif %}
            {% endfor %}
            {% endfor %}
            {% if what_to_watch %}

            ## WATCH
            {{ what_to_watch | truncate_words: 50 }}
            {% endif %}
            """,

        ["compact"] = """
            # {{ date | date: "%Y-%m-%d" }} | {{ vibe }}
            {% for topic in items_by_topic %}
            **{{ topic.name | title_case }}**
            {% for item in topic.items limit: 3 %}- {{ item.title }} ({{ item.url }})
            {% endfor %}
            {% endfor %}
            """,

        ["detailed"] = """
            # Doom-Scroll Digest
            ## {{ date | date: "%B %d, %Y" }}
            ### Vibe: {{ vibe }}

            {% if query %}**Query:** {{ query }}{% endif %}
            **Sources:** {{ sources | join: ", " }}
            **Items:** {{ item_count }}

            {% if overview %}
            ### Overview
            {{ overview }}
            {% endif %}

            {% for topic in items_by_topic %}
            ---
            ## {{ topic.name | title_case }} ({{ topic.items.size }} items)
            {% for item in topic.items limit: 10 %}
            ### [{{ item.title }}]({{ item.url }})
            {{ item.summary }}

            *Sentiment: {{ item.sentiment | sentiment_label }} ({{ item.sentiment }}) | Source: {{ item.source }}*

            {% endfor %}
            {% endfor %}

            {% if what_to_watch %}
            ---
            ## What to Watch
            {{ what_to_watch }}
            {% endif %}
            """,

        ["file"] = """
            ---
            title: Doom-Scroll Digest
            date: {{ date | date: "%Y-%m-%dT%H:%M:%S%z" }}
            vibe: {{ vibe }}
            item_count: {{ item_count }}
            ---

            # Doom-Scroll Digest - {{ date | date: "%B %d, %Y" }}

            {% if overview %}
            ## Summary
            {{ overview }}

            {% endif %}
            {% for topic in items_by_topic %}
            ## {{ topic.name | title_case }}
            {% for item in topic.items limit: 8 %}
            - **[{{ item.title }}]({{ item.url }})**
              {{ item.summary }}
            {% endfor %}

            {% endfor %}
            {% if what_to_watch %}
            ## What to Watch
            {{ what_to_watch }}

            {% endif %}
            ---
            *Generated {{ date | date: "%Y-%m-%dT%H:%M:%S%z" }} with {{ vibe }} vibe*
            """,

        ["email"] = """
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; background: #f5f5f5;">
            <div style="background: white; border-radius: 8px; padding: 24px; box-shadow: 0 2px 4px rgba(0,0,0,0.1);">

            <h1 style="color: #1a1a1a; margin: 0 0 8px 0; font-size: 24px;">Doom-Scroll Digest</h1>
            <p style="color: #666; margin: 0 0 24px 0; font-size: 14px;">{{ date | date: "%B %d, %Y" }} | {{ vibe }} vibe | {{ item_count }} items</p>

            {% if overview %}
            <div style="background: #f8f9fa; border-left: 4px solid #007bff; padding: 12px 16px; margin-bottom: 24px;">
            <p style="margin: 0; color: #333;">{{ overview }}</p>
            </div>
            {% endif %}

            {% for topic in items_by_topic %}
            <h2 style="color: #333; font-size: 18px; margin: 24px 0 12px 0; padding-bottom: 8px; border-bottom: 2px solid #eee;">{{ topic.name | title_case }}</h2>
            {% for item in topic.items limit: 5 %}
            <div style="margin-bottom: 16px;">
            <a href="{{ item.url }}" style="color: #007bff; text-decoration: none; font-weight: 500;">{{ item.title }}</a>
            {% if item.summary %}<p style="color: #666; font-size: 14px; margin: 4px 0 0 0; line-height: 1.5;">{{ item.summary | truncate_words: 25 }}</p>{% endif %}
            </div>
            {% endfor %}
            {% endfor %}

            {% if what_to_watch %}
            <div style="background: #fff3cd; border-radius: 4px; padding: 16px; margin-top: 24px;">
            <h3 style="color: #856404; margin: 0 0 8px 0; font-size: 16px;">What to Watch</h3>
            <p style="color: #856404; margin: 0; font-size: 14px;">{{ what_to_watch | truncate_words: 50 }}</p>
            </div>
            {% endif %}

            </div>
            <p style="text-align: center; color: #999; font-size: 12px; margin-top: 16px;">
            Generated by <a href="https://github.com/scottgal/lucidrag" style="color: #999;">DoomSummarizer</a>
            </p>
            </body>
            </html>
            """,

        ["newsletter"] = """
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
            <body style="font-family: Georgia, serif; max-width: 640px; margin: 0 auto; padding: 40px 20px; background: #fafafa;">
            <div style="background: white; padding: 40px;">

            <div style="text-align: center; margin-bottom: 32px;">
            <h1 style="color: #111; margin: 0; font-size: 28px; letter-spacing: -0.5px;">The Doom Scroll</h1>
            <p style="color: #888; margin: 8px 0 0 0; font-size: 14px; text-transform: uppercase; letter-spacing: 1px;">{{ date | date: "%B %d, %Y" }} Edition</p>
            </div>

            {% if overview %}
            <p style="color: #333; font-size: 18px; line-height: 1.7; margin-bottom: 32px; font-style: italic;">{{ overview }}</p>
            <hr style="border: none; border-top: 1px solid #ddd; margin: 32px 0;">
            {% endif %}

            {% for topic in items_by_topic %}
            <h2 style="color: #111; font-size: 20px; margin: 32px 0 16px 0; font-weight: normal;">{{ topic.name | title_case }}</h2>
            {% for item in topic.items limit: 4 %}
            <article style="margin-bottom: 24px;">
            <h3 style="margin: 0; font-size: 16px;"><a href="{{ item.url }}" style="color: #111; text-decoration: none; border-bottom: 1px solid #ddd;">{{ item.title }}</a></h3>
            {% if item.summary %}<p style="color: #555; font-size: 15px; margin: 8px 0 0 0; line-height: 1.6;">{{ item.summary | truncate_words: 30 }}</p>{% endif %}
            </article>
            {% endfor %}
            {% endfor %}

            {% if what_to_watch %}
            <hr style="border: none; border-top: 1px solid #ddd; margin: 32px 0;">
            <h2 style="color: #111; font-size: 18px; margin-bottom: 12px;">What to Watch</h2>
            <p style="color: #555; font-size: 15px; line-height: 1.6;">{{ what_to_watch }}</p>
            {% endif %}

            <hr style="border: none; border-top: 1px solid #ddd; margin: 32px 0;">
            <p style="color: #999; font-size: 12px; text-align: center;">
            You received this because you subscribed to The Doom Scroll.<br>
            <a href="#" style="color: #999;">Unsubscribe</a> | <a href="#" style="color: #999;">Preferences</a>
            </p>
            </div>
            </body>
            </html>
            """,

        ["slack"] = """
            *Doom-Scroll Digest* | {{ date | date: "%B %d, %Y" }} | _{{ vibe }}_
            {% for topic in items_by_topic %}

            *{{ topic.name | title_case }}*
            {% for item in topic.items limit: 3 %}• <{{ item.url }}|{{ item.title }}>
            {% if item.summary %}  _{{ item.summary | truncate_words: 15 }}_{% endif %}
            {% endfor %}
            {% endfor %}
            {% if what_to_watch %}

            :eyes: *What to Watch*
            {{ what_to_watch | truncate_words: 40 }}
            {% endif %}
            """,

        ["json"] = """
            {
              "date": "{{ date | date: "%Y-%m-%dT%H:%M:%S%z" }}",
              "vibe": "{{ vibe }}",
              "query": "{{ query }}",
              "item_count": {{ item_count }},
              "sources": [{% for source in sources %}"{{ source }}"{% unless forloop.last %}, {% endunless %}{% endfor %}],
              "overview": "{{ overview | escape }}",
              "what_to_watch": "{{ what_to_watch | escape }}",
              "topics": [
            {% for topic in items_by_topic %}    {
                  "name": "{{ topic.name }}",
                  "items": [
            {% for item in topic.items %}        {
                      "title": "{{ item.title | escape }}",
                      "url": "{{ item.url }}",
                      "summary": "{{ item.summary | escape }}",
                      "sentiment": {{ item.sentiment }},
                      "source": "{{ item.source }}"
                    }{% unless forloop.last %},{% endunless %}
            {% endfor %}      ]
                }{% unless forloop.last %},{% endunless %}
            {% endfor %}  ]
            }
            """,

        ["image"] = """
            {% if featured_image %}
            {{ featured_image }}

            {% endif %}
            # {{ items.first.title }}

            {% if items.first.summary %}
            {{ items.first.summary }}
            {% endif %}

            Source: {{ items.first.url }}
            """,

        ["blog-article"] = """
            # {{ article_title }}

            {{ introduction }}

            {% for section in sections %}
            ## {{ section.heading }}

            {{ section.content }}

            {% endfor %}
            ## Conclusion

            {{ conclusion }}

            ---
            ### Sources
            {% for url in source_urls %}- [{{ url }}]({{ url }})
            {% endfor %}

            ---
            *Generated by DoomSummarizer on {{ date | date: "%B %d, %Y" }} | {{ vibe }} vibe*
            """,

        ["blog-timeline"] = """
            # {{ article_title }}

            *A chronological exploration*

            {{ introduction }}

            {% for section in sections %}
            ---

            ## {{ section.heading }}

            {{ section.content }}

            {% endfor %}
            ---

            ## What's Next

            {{ conclusion }}

            ---
            ### Sources
            {% for url in source_urls %}- [{{ url }}]({{ url }})
            {% endfor %}

            *Generated by DoomSummarizer on {{ date | date: "%B %d, %Y" }} | {{ vibe }} vibe*
            """,

        ["blog-newsletter"] = """
            # The Doom Scroll — {{ date | date: "%B %d, %Y" }}
            {% if query %}*Curated: {{ query }}*{% endif %}

            {{ introduction }}

            ---

            {% for pick in top_picks %}
            ## [{{ pick.title }}]({{ pick.url }})
            {{ pick.commentary }}
            *via {{ pick.source }}*

            {% endfor %}
            ---

            ### Quick Hits
            {% for hit in quick_hits %}
            - **[{{ hit.title }}]({{ hit.url }})** — {{ hit.one_liner }}
            {% endfor %}

            ---

            {{ sign_off }}

            *Curated by DoomSummarizer | [Subscribe](#) | [Preferences](#)*
            """,

        ["blog-newsletter-html"] = """
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
            <body style="font-family: Georgia, serif; max-width: 640px; margin: 0 auto; padding: 40px 20px; background: #fafafa;">
            <div style="background: white; padding: 40px;">

            <div style="text-align: center; margin-bottom: 32px;">
            <h1 style="color: #111; margin: 0; font-size: 28px; letter-spacing: -0.5px;">The Doom Scroll</h1>
            <p style="color: #888; margin: 8px 0 0 0; font-size: 14px; text-transform: uppercase; letter-spacing: 1px;">{{ date | date: "%B %d, %Y" }} Edition</p>
            {% if query %}<p style="color: #555; font-size: 16px; margin: 12px 0 0 0;">{{ query }}</p>{% endif %}
            </div>

            <p style="color: #333; font-size: 18px; line-height: 1.7; margin-bottom: 32px; font-style: italic;">{{ introduction }}</p>
            <hr style="border: none; border-top: 1px solid #ddd; margin: 32px 0;">

            {% for pick in top_picks %}
            <article style="margin-bottom: 32px;">
            <h2 style="margin: 0 0 8px 0; font-size: 20px;">
            <a href="{{ pick.url }}" style="color: #111; text-decoration: none; border-bottom: 2px solid #007bff;">{{ pick.title }}</a>
            </h2>
            <p style="color: #333; font-size: 16px; line-height: 1.7; margin: 0 0 8px 0;">{{ pick.commentary }}</p>
            <p style="color: #888; font-size: 13px; margin: 0;">via {{ pick.source }}</p>
            </article>
            {% endfor %}

            <hr style="border: none; border-top: 1px solid #ddd; margin: 32px 0;">

            <h2 style="color: #111; font-size: 18px; margin-bottom: 16px;">Quick Hits</h2>
            {% for hit in quick_hits %}
            <div style="margin-bottom: 12px;">
            <a href="{{ hit.url }}" style="color: #007bff; text-decoration: none; font-weight: 600; font-size: 15px;">{{ hit.title }}</a>
            <span style="color: #666; font-size: 14px;"> — {{ hit.one_liner }}</span>
            </div>
            {% endfor %}

            <hr style="border: none; border-top: 1px solid #ddd; margin: 32px 0;">
            <p style="color: #555; font-size: 15px; line-height: 1.6; font-style: italic;">{{ sign_off }}</p>

            <hr style="border: none; border-top: 1px solid #ddd; margin: 32px 0;">
            <p style="color: #999; font-size: 12px; text-align: center;">
            You received this because you subscribed to The Doom Scroll.<br>
            <a href="#" style="color: #999;">Unsubscribe</a> | <a href="#" style="color: #999;">Preferences</a>
            </p>
            </div>
            </body>
            </html>
            """
    };
}

/// <summary>
/// Data model for template rendering.
/// </summary>
public record DigestData
{
    public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;
    public string Vibe { get; init; } = "neutral";
    public string? Query { get; init; }
    public List<string> Sources { get; init; } = [];
    public string? Overview { get; init; }
    public string? WhatToWatch { get; init; }
    public List<DigestItem> Items { get; init; } = [];
    public int ItemCount => Items.Count;
    public string? FeaturedImage { get; init; }

    // Grouped items for template iteration
    public IEnumerable<TopicGroup> ItemsByTopic =>
        Items.GroupBy(i => i.Topic)
             .OrderByDescending(g => g.Count())
             .Select(g => new TopicGroup(g.Key, g.ToList()));

    // Blog article fields (populated for blog-article / blog-timeline templates)
    public string? ArticleTitle { get; init; }
    public string? Introduction { get; init; }
    public List<DigestSection> Sections { get; init; } = [];
    public string? Conclusion { get; init; }
    public List<string> SourceUrls { get; init; } = [];

    // Newsletter fields (populated for blog-newsletter template)
    public List<DigestPick> TopPicks { get; init; } = [];
    public List<DigestQuickHit> QuickHits { get; init; } = [];
    public string? SignOff { get; init; }
}

public record DigestSection(string Heading, string Content, List<string> SourceUrls);

public record DigestPick(string Title, string Url, string Commentary, string Source);

public record DigestQuickHit(string Title, string Url, string OneLiner);

public record TopicGroup(string Name, List<DigestItem> Items);

public record DigestItem
{
    public string Title { get; init; } = "";
    public string? Url { get; init; }
    public string? Summary { get; init; }
    public string Topic { get; init; } = "general";
    public float Sentiment { get; init; }
    public int Score { get; init; }
    public string? Source { get; init; }
    public string? ImageUrl { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(DigestData))]
[JsonSerializable(typeof(DigestItem))]
[JsonSerializable(typeof(TopicGroup))]
[JsonSerializable(typeof(DigestSection))]
[JsonSerializable(typeof(DigestPick))]
[JsonSerializable(typeof(DigestQuickHit))]
public partial class TemplateJsonContext : JsonSerializerContext;
