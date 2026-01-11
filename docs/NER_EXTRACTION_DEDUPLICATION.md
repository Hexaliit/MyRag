# NER, Feature Extraction & Deduplication

LucidRAG implements a unified extraction pipeline across documents, images, and audio. This document covers the architecture, models, and deduplication strategies.

## Overview

```
                    ┌─────────────────────────────────────────┐
                    │          Unified Pipeline               │
                    └─────────────────────────────────────────┘
                                      │
        ┌─────────────────────────────┼─────────────────────────────┐
        ▼                             ▼                             ▼
  ┌──────────┐                  ┌──────────┐                  ┌──────────┐
  │ Document │                  │  Image   │                  │  Audio   │
  │ Pipeline │                  │ Pipeline │                  │ Pipeline │
  └──────────┘                  └──────────┘                  └──────────┘
        │                             │                             │
        ▼                             ▼                             ▼
  ┌──────────┐                  ┌──────────┐                  ┌──────────┐
  │ ONNX NER │                  │ Florence │                  │ Whisper  │
  │ BERT-NER │                  │   +OCR   │                  │Transcrip.│
  └──────────┘                  └──────────┘                  └──────────┘
        │                             │                             │
        └─────────────────────────────┼─────────────────────────────┘
                                      ▼
                          ┌──────────────────┐
                          │  Entity Merging  │
                          │  Deduplication   │
                          └──────────────────┘
                                      │
                                      ▼
                          ┌──────────────────┐
                          │    PostgreSQL    │
                          │   GraphRAG DB    │
                          └──────────────────┘
```

## 1. Document NER (Named Entity Recognition)

**Location:** `src/Mostlylucid.GraphRag/Extraction/OnnxNerService.cs`

### Model: BERT-base-NER (ONNX)

```csharp
// Model configuration
var modelInfo = NerModelRegistry.BertBaseNer;
// - Model: dslim/bert-base-NER (ONNX exported)
// - Input: tokenized text
// - Output: BIO tags (B-PER, I-PER, B-ORG, I-ORG, B-LOC, I-LOC, B-MISC, I-MISC, O)
// - Max sequence: 512 tokens
```

### Entity Types Extracted

| Tag | Type | Description |
|-----|------|-------------|
| PER | Person | Person names |
| ORG | Organization | Companies, institutions |
| LOC | Location | Places, geographic features |
| MISC | Miscellaneous | Other named entities |

### Processing Flow

```csharp
// 1. Tokenization (BERT WordPiece)
var tokens = tokenizer.Tokenize(text);

// 2. Model inference
var logits = session.Run(inputs);

// 3. BIO tag decoding
var entities = DecodeBioTags(tokens, predictions);

// 4. Entity span extraction
foreach (var entity in entities)
{
    yield return new EntitySpan
    {
        Text = entity.Text,
        Type = entity.Type,
        StartChar = entity.Start,
        EndChar = entity.End,
        Confidence = entity.Score
    };
}
```

### Chunking for Long Documents

```csharp
// Documents longer than 512 tokens are chunked with overlap
const int maxTokens = 400;      // Leave room for special tokens
const int overlapTokens = 50;   // Overlap to catch entities at boundaries

foreach (var chunk in ChunkText(text, maxTokens, overlapTokens))
{
    var entities = await ExtractEntitiesAsync(chunk);
    // Merge entities from overlapping regions
}
```

## 2. Image Feature Extraction

**Location:** `src/ImageSummarizer.Core/Services/`

### 22-Wave Analysis Pipeline

Images go through a comprehensive 22-wave analysis:

| Wave | Service | Features Extracted |
|------|---------|-------------------|
| 1 | BasicImageAnalyzer | Dimensions, format, color mode |
| 2 | ColorAnalyzer | Dominant colors, palette, histogram |
| 3 | CompositionAnalyzer | Rule of thirds, symmetry, balance |
| 4 | EdgeAnalyzer | Edge density, sharpness (OpenCV Sobel) |
| 5 | TextureAnalyzer | Texture patterns, complexity |
| 6 | FaceDetector | Face count, positions, expressions |
| 7 | ObjectDetector | Objects, bounding boxes |
| 8 | SceneClassifier | Scene type (indoor, outdoor, etc.) |
| 9 | OCRService | Text extraction (Tesseract/EasyOCR) |
| 10 | QualityAssessor | Blur, noise, exposure |
| 11 | SalienceDetector | Visual attention regions |
| 12 | MotionAnalyzer | Motion blur, dynamic content |
| 13 | ExifExtractor | Camera metadata, GPS |
| 14 | ColorHarmony | Color relationships |
| 15 | AestheticScorer | Visual appeal score |
| 16 | ContentModeration | Safety classification |
| 17 | StyleAnalyzer | Art style, photography type |
| 18 | SemanticSegmenter | Pixel-level segmentation |
| 19 | DepthEstimator | Depth map generation |
| 20 | CaptionGenerator | Natural language caption |
| 21 | EmbeddingGenerator | CLIP visual embeddings |
| 22 | Florence2Wave | Florence-2 multimodal analysis |

### Florence-2 Integration

```csharp
// Florence-2 provides unified vision-language analysis
var florence = new Florence2Wave();

// Caption generation
var caption = await florence.GenerateCaptionAsync(image);

// Dense region captioning
var regions = await florence.GenerateDenseRegionCaptionsAsync(image);

// OCR with bounding boxes
var ocrResults = await florence.ExtractTextWithRegionsAsync(image);
```

### OCR Pipeline

```csharp
// Multi-engine OCR for robustness
public async Task<OcrResult> ExtractTextAsync(Image image)
{
    // Try Tesseract first (fast, local)
    var tesseractResult = await TesseractOcr.RecognizeAsync(image);

    if (tesseractResult.Confidence < 0.7)
    {
        // Fall back to EasyOCR for complex layouts
        var easyOcrResult = await EasyOcr.RecognizeAsync(image);
        return easyOcrResult.Confidence > tesseractResult.Confidence
            ? easyOcrResult : tesseractResult;
    }

    return tesseractResult;
}
```

## 3. Audio Feature Extraction

### Whisper Transcription

```csharp
// Whisper provides high-quality speech-to-text
var whisper = new WhisperService(modelPath: "whisper-base");

var transcription = await whisper.TranscribeAsync(audioFile);
// Returns: text, timestamps, language detection

// For long audio, chunk by silence detection
var segments = SplitBySilence(audio, minSilenceMs: 500);
foreach (var segment in segments)
{
    var result = await whisper.TranscribeAsync(segment);
    // Merge with timing information
}
```

### Audio Feature Types

| Feature | Description |
|---------|-------------|
| Transcription | Full text from speech |
| Timestamps | Word-level timing |
| Speaker Diarization | Who spoke when |
| Language | Detected language |
| Audio Quality | SNR, clarity metrics |
| Music Detection | Speech vs music segments |
| Emotion | Vocal emotion indicators |

## 4. Entity Deduplication

### Document-Level Deduplication

```csharp
// XxHash64 for fast content hashing
public class ContentHasher
{
    public static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = XxHash64.HashToUInt64(bytes);
        return hash.ToString("x16");
    }
}

// Skip duplicate documents
var existingDoc = await db.Documents
    .FirstOrDefaultAsync(d => d.ContentHash == hash);
if (existingDoc != null)
{
    logger.LogInformation("Skipping duplicate document: {Hash}", hash);
    return existingDoc.Id;
}
```

### Entity Canonical Naming

```csharp
// Normalize entity names for deduplication
public static string CanonicalizeEntity(string name, string type)
{
    var normalized = name
        .ToLowerInvariant()
        .Trim()
        .Replace("  ", " ");

    // Type-specific normalization
    return type switch
    {
        "PER" => NormalizePersonName(normalized),
        "ORG" => NormalizeOrgName(normalized),
        "LOC" => NormalizeLocationName(normalized),
        _ => normalized
    };
}

private static string NormalizePersonName(string name)
{
    // Remove titles: "Dr.", "Mr.", "Mrs.", etc.
    name = TitlePattern().Replace(name, "");
    // Normalize "John Smith" vs "Smith, John"
    // ...
    return name.Trim();
}
```

### Cross-Document Entity Linking

```csharp
// Link entities across documents using fuzzy matching
public async Task<Guid?> FindExistingEntityAsync(string name, string type)
{
    var canonical = CanonicalizeEntity(name, type);

    // Exact match first
    var exact = await db.Entities
        .FirstOrDefaultAsync(e =>
            e.CanonicalName == canonical &&
            e.EntityType == type);

    if (exact != null) return exact.Id;

    // Fuzzy match using trigram similarity (PostgreSQL pg_trgm)
    var fuzzy = await db.Entities
        .Where(e => e.EntityType == type)
        .Where(e => EF.Functions.TrigramsSimilarity(e.CanonicalName, canonical) > 0.7)
        .OrderByDescending(e => EF.Functions.TrigramsSimilarity(e.CanonicalName, canonical))
        .FirstOrDefaultAsync();

    return fuzzy?.Id;
}
```

### Segment Deduplication (SegmentSelector)

**Location:** `src/LucidRAG.Core/Services/SegmentSelector.cs`

```csharp
// Three-stage deduplication:
// 1. Salience filtering - remove low-value segments
// 2. Semantic deduplication - remove near-duplicates by embedding similarity
// 3. Coverage sampling - ensure document coverage

var selector = new SegmentSelector(
    salienceThreshold: 0.05,
    similarityThreshold: 0.80,  // Cosine similarity threshold
    maxSegmentsPerDocument: 250
);

var selected = selector.SelectForEvidence(segments);
// Typical: 372 segments → 50 selected (87% reduction)
```

## 5. Feature Embeddings (pgvector)

**Location:** `src/LucidRAG.Core/Services/FeatureEmbeddingService.cs`

### Semantic Similarity Search

```csharp
// Store feature embeddings in PostgreSQL pgvector
public async Task<Guid> UpsertFeatureAsync(string text, string type)
{
    var embedding = await embeddingService.EmbedAsync(text);

    var feature = new FeatureEmbedding
    {
        FeatureText = text,
        FeatureType = type,
        Embedding = new Vector(embedding)  // pgvector type
    };

    db.FeatureEmbeddings.Add(feature);
    await db.SaveChangesAsync();
    return feature.Id;
}

// Find similar features (e.g., "yellow" → "gold", "amber")
public async Task<List<SimilarFeature>> FindSimilarAsync(string query)
{
    var queryEmbed = await embeddingService.EmbedAsync(query);

    // pgvector cosine distance operator
    var sql = @"
        SELECT ""Id"", ""FeatureText"", (""Embedding"" <=> $1::vector) as distance
        FROM feature_embeddings
        ORDER BY distance LIMIT 10";

    // Execute and convert distance to similarity
    return results.Select(r => new SimilarFeature(
        r.FeatureText,
        1.0 - r.distance  // Cosine similarity
    )).ToList();
}
```

### Query Expansion

```csharp
// Expand user query with similar terms
var query = "car";
var expansions = await featureService.ExpandQueryAsync(query);
// Returns: ["automobile", "vehicle", "sedan", "truck"]

var expandedQuery = $"{query} {string.Join(" ", expansions)}";
// "car automobile vehicle sedan truck"
```

## 6. Intra-Document Segment Graphs

**Location:** `src/LucidRAG.Core/Services/SegmentGraphService.cs`

### Link Types

| Type | Description | Weight |
|------|-------------|--------|
| sequential | Adjacent in document order | 0.7 |
| heading | Same section/heading | 0.8 |
| semantic | High embedding similarity (>0.85) | similarity score |
| entity | Shared entity mentions | 0.6 |
| colocation | Same page/region | 0.5 |

### Graph Construction

```csharp
public async Task<int> BuildGraphAsync(Guid documentId, IReadOnlyList<Segment> segments)
{
    var links = new List<SegmentLink>();

    // 1. Sequential links (adjacent segments)
    for (int i = 0; i < segments.Count - 1; i++)
    {
        links.Add(new SegmentLink
        {
            SourceSegmentHash = segments[i].ContentHash,
            TargetSegmentHash = segments[i + 1].ContentHash,
            LinkType = "sequential",
            Weight = 0.7
        });
    }

    // 2. Heading links (same section)
    var byHeading = segments.GroupBy(s => s.HeadingPath);
    foreach (var group in byHeading.Where(g => g.Count() > 1))
    {
        // Fully connect segments within same heading
    }

    // 3. Semantic links (high similarity)
    for (int i = 0; i < segments.Count; i++)
    {
        for (int j = i + 1; j < segments.Count; j++)
        {
            var similarity = CosineSimilarity(segments[i].Embedding, segments[j].Embedding);
            if (similarity > 0.85)
            {
                links.Add(new SegmentLink { Weight = similarity, LinkType = "semantic" });
            }
        }
    }

    await db.SegmentLinks.AddRangeAsync(links);
    return links.Count;
}
```

### Retrieval Expansion

```csharp
// Expand retrieval results using segment graph
public async Task<List<string>> ExpandRetrievalAsync(
    IEnumerable<string> segmentHashes,
    int expansionDepth = 1)
{
    var expanded = new HashSet<string>(segmentHashes);

    for (int depth = 0; depth < expansionDepth; depth++)
    {
        foreach (var hash in expanded.ToList())
        {
            var connected = await GetConnectedSegmentsAsync(hash);
            var highWeight = connected.Where(c => c.Weight > 0.5);
            expanded.UnionWith(highWeight.Select(c => c.SegmentHash));
        }
    }

    return expanded.ToList();
}
```

### Graph-Based Salience

```csharp
// Segments with more/stronger connections are more salient
public async Task<Dictionary<string, double>> CalculateGraphSalienceAsync(Guid documentId)
{
    var links = await db.SegmentLinks.Where(l => l.DocumentId == documentId).ToListAsync();

    // Weighted degree centrality
    var salience = new Dictionary<string, double>();
    foreach (var link in links)
    {
        salience[link.SourceSegmentHash] += link.Weight;
        salience[link.TargetSegmentHash] += link.Weight;
    }

    // Normalize to 0-1
    var max = salience.Values.Max();
    return salience.ToDictionary(kv => kv.Key, kv => kv.Value / max);
}
```

## 7. Date Extraction

**Location:** `src/LucidRAG.Core/Services/DateExtractionService.cs`

### International Format Support

| Format | Example | Locale |
|--------|---------|--------|
| ISO 8601 | 2024-01-15 | Universal |
| US | 01/15/2024 | American |
| EU | 15/01/2024 | European |
| Long | January 15, 2024 | English |
| EU Long | 15 January 2024 | British |
| Dot | 15.01.2024 | German/Czech |

### Ambiguity Resolution

```csharp
// Detect locale from document content
public LocaleHint DetectLocale(string text)
{
    // Evidence-based detection
    var dmyEvidence = 0;
    var mdyEvidence = 0;

    // Check for impossible values (day > 12 → must be DMY)
    foreach (var date in SlashDates)
    {
        if (date.First > 12 && date.Second <= 12)
            dmyEvidence += 3;  // First is day (DMY)
        else if (date.Second > 12 && date.First <= 12)
            mdyEvidence += 3;  // Second is day (MDY)
    }

    // Language hints
    if (BritishSpellingPattern.IsMatch(text))
        dmyEvidence += 1;  // "colour" → UK → DMY
    if (AmericanSpellingPattern.IsMatch(text))
        mdyEvidence += 1;  // "color" → US → MDY

    return dmyEvidence > mdyEvidence ? LocaleHint.European : LocaleHint.American;
}
```

### Source-Generated Regex (Performance)

```csharp
// All date patterns use source-generated regex
[GeneratedRegex(@"\b(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})\b")]
private static partial Regex Iso8601Pattern();

[GeneratedRegex(@"\b(?<month>January|February|...)\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s*(?<year>\d{4})\b",
    RegexOptions.IgnoreCase)]
private static partial Regex MonthFirstPattern();
```

## 8. Performance Characteristics

### NER Processing

| Operation | Time | Memory |
|-----------|------|--------|
| Model load | 2-3s | ~500MB |
| Per-chunk inference | 10-30ms | ~100MB |
| 10-page document | ~500ms | ~600MB |

### Image Analysis (22-wave)

| Waves | Time | Notes |
|-------|------|-------|
| Basic (1-5) | 50-100ms | CPU only |
| Detection (6-8) | 200-500ms | ONNX inference |
| OCR (9) | 100-300ms | Tesseract |
| Florence-2 (22) | 500-1000ms | GPU recommended |
| **Full pipeline** | 1-3s | Parallel execution |

### Deduplication

| Operation | Time |
|-----------|------|
| XxHash64 (10KB) | <1ms |
| Segment similarity (1000 segments) | 50-100ms |
| Entity fuzzy match | 5-20ms |

---

*Last updated: 2026-01-11*
*Part of LucidRAG v2 architecture*
