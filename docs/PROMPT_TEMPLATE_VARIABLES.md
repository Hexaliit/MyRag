# Prompt Template Variables Reference

This document describes the variables available when building custom prompts in `prompts.yaml` for the LucidRAG unified LLM provider infrastructure.

## Variable Syntax

Variables use curly brace notation: `{variable_name}`

```yaml
template: |
  Query: {query}
  Context: {context}

  Please answer the question based on the context above.
```

Variables are substituted at runtime when you call `GenerateWithPromptAsync`:

```csharp
await provider.GenerateWithPromptAsync("my_prompt", new Dictionary<string, object>
{
    ["query"] = userQuery,
    ["context"] = retrievedContext
});
```

---

## Core Variables by Task Type

### RAG Synthesis Prompts

For generating answers from retrieved document segments.

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{query}` | string | The user's original question | "How does authentication work?" |
| `{question}` | string | Alias for query | Same as above |
| `{segments}` | string | Retrieved text segments formatted with source numbers | "[1] First segment...\n[2] Second segment..." |
| `{context}` | string | Additional context for the query | "User is asking about security features" |
| `{sources}` | string | Formatted source citations | "[1] doc.pdf: Chapter 3\n[2] guide.md: Auth section" |
| `{system_prompt}` | string | Collection-specific system instructions | "You are a technical documentation assistant" |

**Example Prompt:**
```yaml
prompts:
  rag_synthesis:
    system: |
      You are a helpful assistant. Answer questions using ONLY the provided evidence.
      Always cite sources using [N] notation.

    template: |
      QUESTION: {query}

      EVIDENCE FROM DOCUMENTS:
      {segments}

      Synthesize the evidence into a clear, natural response.
      If the evidence doesn't contain relevant information, say so.
```

---

### Query Decomposition Prompts

For breaking complex queries into focused sub-queries.

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{query}` | string | The complex user query | "Compare auth in 2023 vs 2024 audits" |
| `{context}` | string | Additional context | "User has uploaded security documents" |
| `{doc_types}` | string | Available document types | "PDF, DOCX, Markdown" |
| `{collection_name}` | string | Current collection name | "Security Audits" |
| `{schema}` | string | Available data schema info | "documents, entities, relationships" |

**Example Prompt:**
```yaml
prompts:
  query_decomposition:
    json_output: true

    system: |
      You are a query analyst. Decompose complex queries into simpler sub-queries.

    template: |
      Query: {query}

      Available document types: {doc_types}
      Additional context: {context}

      Decompose into 2-5 focused sub-queries.
      Return JSON:
      {
        "sub_queries": [
          {"query": "...", "purpose": "...", "priority": 1}
        ]
      }
```

---

### Entity Extraction Prompts

For extracting entities and relationships from text (GraphRAG).

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{text}` | string | Document text to analyze | "PostgreSQL is used for data storage..." |
| `{chunk}` | string | Specific text chunk | Same as text, smaller scope |
| `{candidates}` | string | Pre-extracted entity candidates | "- PostgreSQL\n- Redis\n- Docker" |
| `{doc_id}` | string | Document identifier | "doc_abc123" |
| `{doc_name}` | string | Document filename | "architecture.md" |

**Example Prompt:**
```yaml
prompts:
  entity_extraction:
    json_output: true

    system: |
      You are an entity extraction system. Extract named entities and relationships.

    template: |
      TEXT:
      {text}

      Extract entities and relationships.
      Return JSON:
      {
        "entities": [
          {"name": "...", "type": "person|organization|technology|concept", "description": "..."}
        ],
        "relationships": [
          {"source": "...", "target": "...", "relationship": "uses|implements|related_to"}
        ]
      }
```

---

### Document Classification Prompts

For classifying document type and characteristics.

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{preview}` | string | First N characters of document | "# Introduction\n\nThis guide covers..." |
| `{filename}` | string | Document filename | "user-guide.pdf" |
| `{extension}` | string | File extension | ".pdf" |
| `{size_kb}` | int | File size in KB | 2048 |
| `{metadata}` | string | Document metadata JSON | "{"author": "John", "created": "2024-01-15"}" |

**Example Prompt:**
```yaml
prompts:
  document_classification:
    json_output: true
    provider: fast-local

    system: |
      You are a document classifier. Analyze document previews.

    template: |
      Filename: {filename}

      First 2000 characters:
      {preview}

      Classify this document:
      {
        "type": "technical|narrative|legal|scientific|business",
        "language": "en|es|fr|de|...",
        "confidence": 0.0-1.0,
        "keywords": ["..."]
      }
```

---

### Image Captioning Prompts

For generating accessible image descriptions.

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{image}` | base64/path | Image data or path | (binary data or file path) |
| `{ocr_text}` | string | Extracted OCR text | "Welcome to our site..." |
| `{detected_objects}` | string | Objects detected in image | "person, laptop, desk" |
| `{colors}` | string | Dominant colors | "blue, white, gray" |
| `{dimensions}` | string | Image dimensions | "1920x1080" |

**Example Prompt:**
```yaml
prompts:
  image_caption:
    provider: vision

    system: |
      Generate a concise, WCAG-compliant alt-text caption (max 125 chars).
      Be specific and descriptive. Don't use "Image shows" or "Picture of".

    template: |
      Analyze this image.

      OCR text detected: {ocr_text}
      Objects detected: {detected_objects}

      Provide a caption suitable for screen readers.
```

---

### Community Summarization Prompts

For summarizing knowledge graph communities.

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{entities}` | string | List of entities in community | "- PostgreSQL (database)\n- Redis (cache)" |
| `{relationships}` | string | Relationships between entities | "- PostgreSQL --[stores]--> UserData" |
| `{entity_types}` | string | Types present in community | "database(2), service(3), concept(1)" |
| `{key_terms}` | string | Key terms from community | "caching, performance, storage" |
| `{member_count}` | int | Number of entities | 15 |

**Example Prompt:**
```yaml
prompts:
  community_summary:
    system: |
      Summarize knowledge graph communities concisely.

    template: |
      Entities in community:
      {entities}

      Relationships:
      {relationships}

      Entity types: {entity_types}
      Key terms: {key_terms}

      Provide:
      1. A 2-3 word title (e.g., "Database Optimization")
      2. A 2-3 sentence summary of this community's theme

      Format:
      NAME: [title]
      SUMMARY: [description]
```

---

### Query Clarification Prompts

For identifying ambiguous queries that need clarification.

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{query}` | string | The potentially ambiguous query | "How do I fix the error?" |
| `{doc_types}` | string | Available document types | "API docs, Tutorials, FAQs" |
| `{recent_queries}` | string | Recent conversation queries | "Previous: 'What is X?'" |
| `{available_topics}` | string | Topics in the corpus | "authentication, database, deployment" |

**Example Prompt:**
```yaml
prompts:
  query_clarification:
    json_output: true

    system: |
      Analyze queries for ambiguity and suggest clarifications.

    template: |
      User query: {query}

      Available document types: {doc_types}
      Available topics: {available_topics}

      Analyze if this query is ambiguous:
      {
        "is_ambiguous": true|false,
        "ambiguity_score": 0.0-1.0,
        "clarification_questions": ["Which specific error?", "..."],
        "suggested_refinements": ["authentication error", "database connection error"]
      }
```

---

### Summary Generation Prompts

For generating document summaries.

| Variable | Type | Description | Example Value |
|----------|------|-------------|---------------|
| `{document}` | string | Full document text | "# Chapter 1\n\nThis document..." |
| `{length}` | int/string | Desired summary length | "3" or "short" |
| `{focus}` | string | Specific focus area | "technical implementation details" |
| `{audience}` | string | Target audience | "developers" or "executives" |

**Example Prompt:**
```yaml
prompts:
  summary:
    system: |
      Create clear, concise summaries that capture key points and main arguments.

    template: |
      Document:
      {document}

      Create a summary in {length} sentences.
      Focus on: {focus}
      Target audience: {audience}
```

---

## Provider-Specific Overrides

Different backends may need different settings for the same prompt:

```yaml
prompts:
  complex_analysis:
    template: |
      Analyze: {text}

    overrides:
      anthropic:
        max_tokens: 4096      # Claude can handle longer output
        temperature: 0.3

      openai:
        max_tokens: 4096
        temperature: 0.3

      ollama:
        max_tokens: 2048      # Smaller local models
        temperature: 0.1      # More deterministic for local
```

---

## Variable Formatting Patterns

### Formatted Lists

When passing lists, format them clearly:

```csharp
var entities = new[] { "PostgreSQL", "Redis", "Docker" };
var formattedEntities = string.Join("\n", entities.Select((e, i) => $"- {e}"));

await provider.GenerateWithPromptAsync("my_prompt", new Dictionary<string, object>
{
    ["entities"] = formattedEntities  // "- PostgreSQL\n- Redis\n- Docker"
});
```

### Numbered Sources

For RAG synthesis with citations:

```csharp
var sources = results.Select((r, i) =>
    $"[{i + 1}] {r.Text}");
var formattedSources = string.Join("\n\n", sources);

await provider.GenerateWithPromptAsync("rag_synthesis", new Dictionary<string, object>
{
    ["segments"] = formattedSources,
    ["query"] = userQuery
});
```

### Truncated Content

For large documents, truncate intelligently:

```csharp
var preview = document.Text.Length > 2000
    ? document.Text[..2000] + "..."
    : document.Text;

await provider.GenerateWithPromptAsync("classify", new Dictionary<string, object>
{
    ["preview"] = preview
});
```

---

## JSON Output Prompts

For structured output, set `json_output: true` and provide a schema:

```yaml
prompts:
  structured_extraction:
    json_output: true

    template: |
      Text: {text}

      Extract information in this exact JSON format:
      {
        "title": "extracted title",
        "topics": ["topic1", "topic2"],
        "sentiment": "positive|negative|neutral",
        "confidence": 0.0-1.0
      }
```

Use with typed deserialization:

```csharp
public class ExtractionResult
{
    public string Title { get; set; }
    public List<string> Topics { get; set; }
    public string Sentiment { get; set; }
    public double Confidence { get; set; }
}

var result = await provider.GenerateJsonWithPromptAsync<ExtractionResult>(
    "structured_extraction",
    new Dictionary<string, object> { ["text"] = inputText });
```

---

## Best Practices

### 1. Use Descriptive Variable Names

```yaml
# Good
template: |
  User question: {user_query}
  Retrieved evidence: {evidence_segments}

# Avoid
template: |
  Q: {q}
  E: {e}
```

### 2. Provide Clear Instructions in System Prompt

```yaml
system: |
  You are an expert technical writer.
  - Be concise and precise
  - Use technical terminology appropriately
  - Cite sources when available
```

### 3. Include Output Format Examples

```yaml
template: |
  {input_text}

  Respond in this format:
  SUMMARY: [2-3 sentence summary]
  KEY_POINTS:
  - [point 1]
  - [point 2]
  CONFIDENCE: [high/medium/low]
```

### 4. Handle Missing Variables Gracefully

The system keeps placeholders for missing variables:

```csharp
// If 'context' is not provided, template keeps {context} as-is
await provider.GenerateWithPromptAsync("my_prompt", new Dictionary<string, object>
{
    ["query"] = userQuery
    // context is missing - will show warning in logs
});
```

### 5. Use Provider Overrides for Optimization

```yaml
overrides:
  ollama:
    temperature: 0.1    # More deterministic for smaller models
    max_tokens: 1024    # Limit output for faster response

  anthropic:
    temperature: 0.3    # Claude handles nuance well
    max_tokens: 4096    # Allow longer, more detailed responses
```

---

## Complete Example

Here's a complete custom prompt example:

```yaml
prompts:
  code_review:
    name: code_review
    description: "Review code for issues and improvements"
    version: 1
    json_output: true

    system: |
      You are an expert code reviewer. Analyze code for:
      - Bugs and potential issues
      - Performance problems
      - Security vulnerabilities
      - Code style and best practices

      Be constructive and specific in your feedback.

    template: |
      Programming Language: {language}

      Code to review:
      ```{language}
      {code}
      ```

      Context: {context}

      Provide a structured review:
      {
        "overall_quality": "excellent|good|needs_improvement|poor",
        "issues": [
          {
            "severity": "critical|warning|info",
            "line": null,
            "description": "...",
            "suggestion": "..."
          }
        ],
        "positive_aspects": ["..."],
        "refactoring_suggestions": ["..."]
      }

    overrides:
      anthropic:
        max_tokens: 4096
        temperature: 0.2
      ollama:
        max_tokens: 2048
        temperature: 0.1
```

Usage:

```csharp
var result = await provider.GenerateJsonWithPromptAsync<CodeReviewResult>(
    "code_review",
    new Dictionary<string, object>
    {
        ["language"] = "csharp",
        ["code"] = sourceCode,
        ["context"] = "This is a background job processor"
    });
```

---

## Related Documentation

- [Unified LLM Providers](UNIFIED_LLM_PROVIDERS.md) - Full LLM infrastructure documentation
- [YAML Manifest System](yaml-manifest-system.md) - Wave/lens manifest configuration
