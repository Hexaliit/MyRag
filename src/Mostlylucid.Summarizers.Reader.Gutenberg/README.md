# Mostlylucid.Summarizers.Reader.Gutenberg

Project Gutenberg archive reader for the Summarizers pipeline.

## Features

- **ZIP Archive Support**: Reads ZIP archives containing `.txt` or `.html` files
- **Boilerplate Stripping**: Removes Project Gutenberg header and footer boilerplate
- **Metadata Extraction**: Title, author, release date, language, eBook number from PG headers
- **HTML Handling**: Strips HTML tags from Gutenberg HTML archives, preserving paragraph structure
- **Encoding Detection**: UTF-8 with fallback to Latin-1
- **Word Count**: Zero-allocation word counting for extracted content

## Usage

```csharp
services.AddGutenbergReader();

var reader = serviceProvider.GetRequiredService<IDocumentReader>();
var result = await reader.ReadAsync("pg84.zip");

// Metadata from PG header
Console.WriteLine(result.Metadata["title"]);   // "Frankenstein; Or, The Modern Prometheus"
Console.WriteLine(result.Metadata["author"]);   // "Mary Wollstonecraft Shelley"
Console.WriteLine(result.Metadata["language"]); // "English"
```

## Supported Extensions

- `.zip` (Project Gutenberg archives)

## Dependencies

- `System.IO.Compression` (built-in)
- [Mostlylucid.DoomSummarizer.Core](https://www.nuget.org/packages/Mostlylucid.DoomSummarizer.Core) - IDocumentReader interface

## License

MIT
