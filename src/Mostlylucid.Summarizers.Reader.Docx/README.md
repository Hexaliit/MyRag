# Mostlylucid.LucidRAG.Readers.Docx

DOCX document reader for the Summarizers pipeline.

## Features

- **Heading-Aware Extraction**: Preserves document structure with Markdown heading levels
- **List Support**: Ordered and unordered lists converted to Markdown
- **Table Extraction**: Tables converted to Markdown table format
- **Text Formatting**: Bold, italic, and other formatting preserved
- **Metadata Extraction**: Title, author, subject from document properties
- **Word Count**: Zero-allocation word counting for extracted content

## Usage

```csharp
services.AddDocxReader();

var reader = serviceProvider.GetRequiredService<IDocumentReader>();
var result = await reader.ReadAsync("document.docx");
```

## Supported Extensions

- `.docx`

## Dependencies

- [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) - DOCX parsing
- [Mostlylucid.LucidRAG.DoomSummarizer.Core](https://www.nuget.org/packages/Mostlylucid.LucidRAG.DoomSummarizer.Core) -
  IDocumentReader interface

## License

MIT
