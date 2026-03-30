# Mostlylucid.LucidRAG.Core

Core business logic for LucidRAG - shared by web, CLI, and API projects.

## Features

- **Document Processing**: Orchestrates document ingestion, summarization, and indexing
- **Conversation Service**: Multi-turn agentic RAG conversations with citation grounding
- **Entity Graph**: Knowledge graph querying and visualization
- **Search**: Agentic search with query decomposition and RRF fusion
- **EF Core**: PostgreSQL and SQLite database support with migrations
- **Identity**: ASP.NET Core Identity integration for user management

## Installation

```bash
dotnet add package Mostlylucid.LucidRAG.Core
```

## Usage

```csharp
// Register all LucidRAG services
builder.Services.AddLucidRagCore(builder.Configuration);

// Inject and use
public class MyController(ConversationService conversations)
{
    public async Task<string> AskAsync(string question)
    {
        var response = await conversations.AskAsync(question);
        return response.Answer;
    }
}
```

## Dependencies

This is the top-level orchestration package that brings together all LucidRAG components:
DocSummarizer, GraphRag, RAG, ImageSummarizer, VideoSummarizer, DataSummarizer,
DomainClassifier, DoomSummarizer.Core, and LLM providers (Anthropic, OpenAI).

## Links

- [Repository](https://github.com/scottgal/lucidrag)
- [LucidRAG Documentation](https://github.com/scottgal/lucidrag#readme)
