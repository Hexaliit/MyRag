# Mostlylucid.LucidRAG.GraphRag

Entity extraction and knowledge graph construction for LucidRAG.

## Features

- **ONNX NER**: Local named entity recognition using ONNX models (no external API needed)
- **Knowledge Graph**: Entity and relationship extraction from documents
- **DuckDB Storage**: Compact graph storage using DuckDB
- **Pipeline Integration**: Works with DocSummarizer for document-level entity extraction

## Installation

```bash
dotnet add package Mostlylucid.LucidRAG.GraphRag
```

## Usage

```csharp
// Inject the pipeline
public class MyService(GraphRagPipeline pipeline)
{
    public async Task ExtractEntitiesAsync(string text)
    {
        var entities = await pipeline.ExtractEntitiesAsync(text);
        foreach (var entity in entities)
        {
            Console.WriteLine($"{entity.Name} ({entity.Type})");
        }
    }
}
```

## Dependencies

- `Mostlylucid.LucidRAG.DocSummarizer` - Document processing and embedding infrastructure
- `DuckDB.NET.Data.Full` - Embedded graph storage
- `Markdig` - Markdown parsing

## Links

- [Repository](https://github.com/scottgal/lucidrag)
- [LucidRAG Documentation](https://github.com/scottgal/lucidrag#readme)
