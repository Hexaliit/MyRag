# Mostlylucid.LucidRAG.Readers.Docling

Docling-based document reader for the Summarizers pipeline.

## Features

- **OCR Support**: Handles scanned PDFs and images via Docling service
- **Layout Analysis**: Structure-aware extraction preserving document hierarchy
- **Table Extraction**: High-quality table extraction from complex layouts
- **Multi-Format**: Supports PDF, DOCX, and other formats through Docling
- **Word Count**: Zero-allocation word counting for extracted content

## Prerequisites

Requires a running [Docling](https://github.com/DS4SD/docling) service instance. Configure the endpoint URL via options.

## Usage

```csharp
services.AddDoclingReader(options =>
{
    options.EndpointUrl = "http://localhost:5001";
});

var reader = serviceProvider.GetRequiredService<IDocumentReader>();
var result = await reader.ReadAsync("scanned-document.pdf");
```

## Supported Extensions

- `.pdf`
- `.docx`
- `.pptx`
- `.html`

## Dependencies

- Docling service (external)
- [Mostlylucid.LucidRAG.DoomSummarizer.Core](https://www.nuget.org/packages/Mostlylucid.LucidRAG.DoomSummarizer.Core) -
  IDocumentReader interface

## License

MIT
