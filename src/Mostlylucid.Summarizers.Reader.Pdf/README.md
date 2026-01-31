# Mostlylucid.Summarizers.Reader.Pdf

PDF document reader for the Summarizers pipeline.

## Features

- **Text Extraction**: Full text extraction from PDF documents using PdfPig
- **Page Markers**: Optional page boundary markers in output for downstream splitting
- **Metadata Extraction**: Title, author, subject, keywords from PDF document properties
- **Paragraph Normalization**: Joins hyphenated words and normalizes whitespace
- **Word Count**: Zero-allocation word counting for extracted content

## Usage

```csharp
services.AddPdfReader();

// Inject and use
var reader = serviceProvider.GetRequiredService<IDocumentReader>();
var result = await reader.ReadAsync("document.pdf");

Console.WriteLine(result.Title);
Console.WriteLine(result.WordCount);
Console.WriteLine(result.Markdown);
```

## Supported Extensions

- `.pdf`

## Dependencies

- [PdfPig](https://github.com/UglyToad/PdfPig) - PDF text extraction
- [Mostlylucid.DoomSummarizer.Core](https://www.nuget.org/packages/Mostlylucid.DoomSummarizer.Core) - IDocumentReader interface

## License

MIT
