using YamlDotNet.Serialization;

namespace DoomSummarizer.Models;

/// <summary>
///     YAML-driven template definition that controls both LLM response structure
///     and output rendering. Templates define what sections the LLM should produce,
///     how the outline is generated, and how the final output is rendered.
/// </summary>
public class TemplateDefinition
{
    /// <summary>Template identifier (file name without extension).</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>Human-readable description.</summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>
    ///     Instructions for the sentinel LLM when generating the article outline.
    ///     Overrides the default outline instructions in SynthesizeBlogArticleAsync.
    /// </summary>
    [YamlMember(Alias = "outline_instructions")]
    public string? OutlineInstructions { get; set; }

    /// <summary>
    ///     Defines the required sections and per-section prompts.
    ///     When provided, overrides the sentinel-generated outline with a fixed structure.
    /// </summary>
    [YamlMember(Alias = "sections")]
    public List<TemplateSectionDef> Sections { get; set; } = [];

    /// <summary>
    ///     Prompt guidance for the introduction paragraph.
    /// </summary>
    [YamlMember(Alias = "introduction")]
    public TemplatePromptDef? Introduction { get; set; }

    /// <summary>
    ///     Prompt guidance for the conclusion paragraph.
    /// </summary>
    [YamlMember(Alias = "conclusion")]
    public TemplatePromptDef? Conclusion { get; set; }

    /// <summary>
    ///     Optional Liquid template for rendering. If omitted, the built-in
    ///     "blog-article" template is used.
    /// </summary>
    [YamlMember(Alias = "template")]
    public string? Template { get; set; }

    /// <summary>
    ///     Name of a built-in template to use for rendering (e.g., "blog-article", "blog-timeline").
    ///     Only used when <see cref="Template" /> is null.
    /// </summary>
    [YamlMember(Alias = "base_template")]
    public string? BaseTemplate { get; set; }

    /// <summary>
    ///     Name of the system prompt template to use (e.g., "research-synthesis").
    ///     If null, the default "system-synthesis" prompt is used.
    /// </summary>
    [YamlMember(Alias = "system_prompt")]
    public string? SystemPrompt { get; set; }

    /// <summary>
    ///     Name of the section prompt template to use (e.g., "research-section").
    ///     If null, the default "blog-section" prompt is used.
    /// </summary>
    [YamlMember(Alias = "section_prompt_template")]
    public string? SectionPromptTemplate { get; set; }

    /// <summary>Whether sections are fixed (skip sentinel outline generation).</summary>
    public bool HasFixedSections => Sections.Count > 0;
}

/// <summary>
///     Defines a section in the template's LLM output structure.
/// </summary>
public class TemplateSectionDef
{
    /// <summary>Section heading (e.g., "The Problem", "Solution").</summary>
    [YamlMember(Alias = "heading")]
    public string Heading { get; set; } = "";

    /// <summary>
    ///     Prompt instructions for the LLM when generating this section.
    ///     Describes what content to extract from evidence.
    /// </summary>
    [YamlMember(Alias = "prompt")]
    public string Prompt { get; set; } = "";

    /// <summary>Target word count for this section (guidance, not hard limit).</summary>
    [YamlMember(Alias = "target_words")]
    public int TargetWords { get; set; } = 250;

    /// <summary>
    ///     Optional evidence selection hint. Controls which evidence items
    ///     are prioritized for this section (e.g., "highest_relevance", "newest", "oldest").
    /// </summary>
    [YamlMember(Alias = "evidence_strategy")]
    public string? EvidenceStrategy { get; set; }
}

/// <summary>
///     Prompt guidance for introduction or conclusion paragraphs.
/// </summary>
public class TemplatePromptDef
{
    /// <summary>LLM prompt instructions.</summary>
    [YamlMember(Alias = "prompt")]
    public string Prompt { get; set; } = "";

    /// <summary>Target word count.</summary>
    [YamlMember(Alias = "target_words")]
    public int TargetWords { get; set; } = 100;
}