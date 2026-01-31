# Mostlylucid.LucidRAG.Plugins.Books

Book summarizer plugin for DoomSummarizer. Provides hierarchical document splitting and book type detection for long-form documents.

## Features

- **Hierarchical Splitting**: Adaptive depth splitting based on document length and structure
  - Novels: Parts > Chapters > Sections
  - Plays: Acts > Scenes
  - Anthologies: Works > Chapters > Sections
  - Academic: Abstract > Introduction > Methods > Results > Discussion
- **Book Type Detection**: Signal-based classification (fiction, nonfiction, academic, technical, play, anthology, collection)
- **Pattern-Based Detection**: YAML-defined regex patterns for chapter/act/scene boundaries
- **Chapter-Aware Templates**: Summarization templates for chapter-by-chapter and synthesis strategies
- **Embedded Resources**: Prompts, templates, strategies, and patterns shipped as embedded YAML/TXT

## Usage

```csharp
// Register the plugin
var plugin = new BookProcessorPlugin(logger);
registry.Register(plugin);

// Detection
var detection = BookTypeDetector.Detect(content, "pride-and-prejudice.txt");
Console.WriteLine(detection.Type);       // "fiction"
Console.WriteLine(detection.Confidence); // 0.85

// Splitting
var splitter = new HierarchicalBookSplitter(logger);
var tree = await splitter.SplitAsync(content, new SplitOptions { PatternName = "novel" });

foreach (var chapter in tree.Children)
    Console.WriteLine($"{chapter.Title} ({chapter.TotalWordCount} words)");
```

## Supported Document Types

| Type | Detection Signals |
|------|------------------|
| Fiction | Chapter markers, dialogue, narrative patterns, word count |
| Nonfiction | Informational structure, no fiction signals |
| Academic | Abstract/Introduction/Methods sections, citations |
| Technical | Code blocks, numbered steps, reference sections |
| Play | Act/Scene markers, character dialogue |
| Anthology | Multiple work boundaries, very long documents |
| Collection | Poetry line patterns, multiple short works |

## Dependencies

- [Mostlylucid.LucidRAG.DoomSummarizer.Core](https://www.nuget.org/packages/Mostlylucid.LucidRAG.DoomSummarizer.Core) - Plugin interfaces and document model
- [YamlDotNet](https://github.com/aaubry/YamlDotNet) - Pattern and strategy YAML parsing

## License

MIT
