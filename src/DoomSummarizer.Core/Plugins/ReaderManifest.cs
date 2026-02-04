using YamlDotNet.Serialization;

namespace DoomSummarizer.Plugins;

/// <summary>
///     YAML manifest shipped with each reader package.
///     Declares the reader's capabilities, extensions, and configuration options.
///     Loaded from embedded resource "reader.yaml" during plugin initialization.
/// </summary>
public class ReaderManifest
{
    /// <summary>Unique reader identifier (e.g. "pdf", "docx", "gutenberg").</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>Human-readable display name.</summary>
    [YamlMember(Alias = "display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>Short description of what this reader does.</summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>Reader version.</summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>File extensions this reader handles (e.g. [".pdf", ".PDF"]).</summary>
    [YamlMember(Alias = "extensions")]
    public List<string> Extensions { get; set; } = [];

    /// <summary>MIME types this reader handles.</summary>
    [YamlMember(Alias = "mime_types")]
    public List<string> MimeTypes { get; set; } = [];

    /// <summary>Priority when multiple readers match (higher wins).</summary>
    [YamlMember(Alias = "priority")]
    public int Priority { get; set; } = 100;

    /// <summary>NuGet package dependencies (informational).</summary>
    [YamlMember(Alias = "dependencies")]
    public List<string> Dependencies { get; set; } = [];

    /// <summary>Capabilities this reader supports.</summary>
    [YamlMember(Alias = "capabilities")]
    public ReaderCapabilities Capabilities { get; set; } = new();

    /// <summary>Default configuration values.</summary>
    [YamlMember(Alias = "defaults")]
    public Dictionary<string, string> Defaults { get; set; } = new();
}

/// <summary>
///     Capabilities a reader can declare in its manifest.
/// </summary>
public class ReaderCapabilities
{
    /// <summary>Can extract tables from documents.</summary>
    [YamlMember(Alias = "tables")]
    public bool Tables { get; set; }

    /// <summary>Can extract images/figures.</summary>
    [YamlMember(Alias = "images")]
    public bool Images { get; set; }

    /// <summary>Preserves document structure (headings, sections).</summary>
    [YamlMember(Alias = "structure")]
    public bool Structure { get; set; }

    /// <summary>Supports reading from streams (not just files).</summary>
    [YamlMember(Alias = "streaming")]
    public bool Streaming { get; set; } = true;

    /// <summary>Extracts metadata (title, author, dates).</summary>
    [YamlMember(Alias = "metadata")]
    public bool Metadata { get; set; } = true;

    /// <summary>Supports OCR for scanned content.</summary>
    [YamlMember(Alias = "ocr")]
    public bool Ocr { get; set; }

    /// <summary>Can handle page-based documents (PDFs).</summary>
    [YamlMember(Alias = "pages")]
    public bool Pages { get; set; }
}