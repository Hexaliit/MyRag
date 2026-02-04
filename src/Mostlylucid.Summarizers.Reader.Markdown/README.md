# Mostlylucid.LucidRAG.Readers.Markdown

Markdown and plain text reader for the Summarizers pipeline.

## Features

- **YAML Front Matter Parsing**: Extracts title, author, date, tags from front matter
- **Heading Title Detection**: Uses the first `# H1` heading as the document title
- **Plain Text Support**: Handles `.txt` files alongside `.md`
- **Word Count**: Zero-allocation word counting for extracted content

## Usage

```csharp
services.AddMarkdownReader();

var reader = serviceProvider.GetRequiredService<IDocumentReader>();
var result = await reader.ReadAsync("document.md");

Console.WriteLine(result.Title);    // From front matter or first heading
Console.WriteLine(result.Markdown); // Content (front matter stripped)
```

## Supported Extensions

- `.md`
- `.txt`

## Dependencies

- [Mostlylucid.LucidRAG.DoomSummarizer.Core](https://www.nuget.org/packages/Mostlylucid.LucidRAG.DoomSummarizer.Core) -
  IDocumentReader interface

## License

MIT
