using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DoomSummarizer.Services;

/// <summary>
///     Client for the Semantic Scholar Academic Graph API.
///     Provides citation traversal (who cites X, what does X cite) and bulk search
///     with optional filtering by year, citation count, and fields of study.
///     https://api.semanticscholar.org/api-docs/graph
/// </summary>
public class SemanticScholarClient
{
    private const string BaseUrl = "https://api.semanticscholar.org/graph/v1";
    private const string PaperFields = "paperId,title,authors,year,abstract,citationCount,influentialCitationCount,fieldsOfStudy,tldr,externalIds,venue,publicationVenue";
    private const string CitationFields = "paperId,title,authors,year,abstract,citationCount,influentialCitationCount,fieldsOfStudy,externalIds,isInfluential";

    private readonly HttpClient _httpClient;
    private readonly ILogger<SemanticScholarClient> _logger;
    private readonly ConcurrentDictionary<string, S2Paper?> _cache = new();

    // System minimum: 1 req/sec even with API key (S2 documented limit).
    // This is an internal safety net; ApiRateLimiter also enforces a floor for "semantic_scholar".
    private static readonly TimeSpan SystemMinRequestInterval = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public SemanticScholarClient(HttpClient httpClient, ILogger<SemanticScholarClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    ///     Configure API key for dedicated rate limit (1 req/sec guaranteed).
    ///     Without key: shared pool of 5000 req/5min across all unauthenticated users.
    /// </summary>
    public void SetApiKey(string apiKey)
    {
        _httpClient.DefaultRequestHeaders.Remove("x-api-key");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    /// <summary>
    ///     Search for papers by keyword query with optional filters.
    /// </summary>
    public async Task<List<S2Paper>> SearchAsync(
        string query,
        int limit = 20,
        int? yearFrom = null,
        int? yearTo = null,
        int? minCitations = null,
        string? fieldsOfStudy = null,
        CancellationToken ct = default)
    {
        await RateLimitAsync(ct);

        try
        {
            var url = $"{BaseUrl}/paper/search?query={Uri.EscapeDataString(query)}&limit={limit}&fields={PaperFields}";

            if (yearFrom.HasValue || yearTo.HasValue)
            {
                var yearRange = $"{yearFrom?.ToString() ?? ""}-{yearTo?.ToString() ?? ""}";
                url += $"&year={yearRange}";
            }

            if (minCitations.HasValue)
                url += $"&minCitationCount={minCitations.Value}";

            if (!string.IsNullOrEmpty(fieldsOfStudy))
                url += $"&fieldsOfStudy={Uri.EscapeDataString(fieldsOfStudy)}";

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Semantic Scholar search returned {Status} for query '{Query}'",
                    response.StatusCode, query);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<S2SearchResponse>(json, S2JsonOptions.Default);

            var papers = result?.Data ?? [];
            foreach (var paper in papers)
                if (!string.IsNullOrEmpty(paper.PaperId))
                    _cache[paper.PaperId] = paper;

            _logger.LogDebug("S2 search '{Query}': {Count} results", query, papers.Count);
            return papers;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic Scholar search failed for '{Query}'", query);
            return [];
        }
    }

    /// <summary>
    ///     Get papers that cite the given paper (reverse citations).
    ///     This is the key capability not available from arXiv alone.
    /// </summary>
    public async Task<List<S2Citation>> GetCitationsAsync(
        string paperId,
        int limit = 100,
        CancellationToken ct = default)
    {
        await RateLimitAsync(ct);

        try
        {
            var url = $"{BaseUrl}/paper/{Uri.EscapeDataString(paperId)}/citations?limit={limit}&fields={CitationFields}";

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("S2 citations returned {Status} for {PaperId}", response.StatusCode, paperId);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<S2CitationResponse>(json, S2JsonOptions.Default);

            var citations = result?.Data?
                .Where(c => c.CitingPaper != null)
                .Select(c => new S2Citation(c.CitingPaper!, c.IsInfluential))
                .ToList() ?? [];

            _logger.LogDebug("S2 citations for {PaperId}: {Count} papers cite it", paperId, citations.Count);
            return citations;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get citations for {PaperId}", paperId);
            return [];
        }
    }

    /// <summary>
    ///     Get papers cited by the given paper (forward references).
    /// </summary>
    public async Task<List<S2Citation>> GetReferencesAsync(
        string paperId,
        int limit = 100,
        CancellationToken ct = default)
    {
        await RateLimitAsync(ct);

        try
        {
            var url = $"{BaseUrl}/paper/{Uri.EscapeDataString(paperId)}/references?limit={limit}&fields={CitationFields}";

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("S2 references returned {Status} for {PaperId}", response.StatusCode, paperId);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<S2ReferenceResponse>(json, S2JsonOptions.Default);

            var references = result?.Data?
                .Where(r => r.CitedPaper != null)
                .Select(r => new S2Citation(r.CitedPaper!, r.IsInfluential))
                .ToList() ?? [];

            _logger.LogDebug("S2 references for {PaperId}: {Count} papers referenced", paperId, references.Count);
            return references;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get references for {PaperId}", paperId);
            return [];
        }
    }

    /// <summary>
    ///     Get paper details by ID. Supports ARXIV:, DOI:, or raw S2 paper ID.
    /// </summary>
    public async Task<S2Paper?> GetPaperAsync(string paperId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(paperId, out var cached))
            return cached;

        await RateLimitAsync(ct);

        try
        {
            var url = $"{BaseUrl}/paper/{Uri.EscapeDataString(paperId)}?fields={PaperFields}";

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("S2 paper lookup returned {Status} for {PaperId}", response.StatusCode, paperId);
                _cache[paperId] = null;
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var paper = JsonSerializer.Deserialize<S2Paper>(json, S2JsonOptions.Default);

            if (paper != null && !string.IsNullOrEmpty(paper.PaperId))
                _cache[paper.PaperId] = paper;
            _cache[paperId] = paper;

            return paper;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get paper {PaperId}", paperId);
            _cache[paperId] = null;
            return null;
        }
    }

    /// <summary>
    ///     Build a Semantic Scholar paper ID from an arXiv ID.
    /// </summary>
    public static string ArxivToS2Id(string arxivId) => $"ARXIV:{AcademicPatterns.StripArxivVersion(arxivId)}";

    /// <summary>
    ///     Build a Semantic Scholar paper ID from a DOI.
    /// </summary>
    public static string DoiToS2Id(string doi) => $"DOI:{doi}";

    private async Task RateLimitAsync(CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < SystemMinRequestInterval)
                await Task.Delay(SystemMinRequestInterval - elapsed, ct);

            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}

// ── Response Models ──────────────────────────────────────────────────

public class S2Paper
{
    [JsonPropertyName("paperId")]
    public string? PaperId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("authors")]
    public List<S2Author>? Authors { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("abstract")]
    public string? Abstract { get; set; }

    [JsonPropertyName("citationCount")]
    public int CitationCount { get; set; }

    [JsonPropertyName("influentialCitationCount")]
    public int InfluentialCitationCount { get; set; }

    [JsonPropertyName("fieldsOfStudy")]
    public List<string>? FieldsOfStudy { get; set; }

    [JsonPropertyName("tldr")]
    public S2Tldr? Tldr { get; set; }

    [JsonPropertyName("externalIds")]
    public S2ExternalIds? ExternalIds { get; set; }

    [JsonPropertyName("venue")]
    public string? Venue { get; set; }

    [JsonPropertyName("publicationVenue")]
    public S2PublicationVenue? PublicationVenue { get; set; }

    /// <summary>Extract arXiv ID from external IDs if present.</summary>
    public string? ArxivId => ExternalIds?.ArXiv;

    /// <summary>Extract DOI from external IDs if present.</summary>
    public string? Doi => ExternalIds?.DOI;
}

public class S2Author
{
    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class S2Tldr
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public class S2ExternalIds
{
    [JsonPropertyName("ArXiv")]
    public string? ArXiv { get; set; }

    [JsonPropertyName("DOI")]
    public string? DOI { get; set; }

    [JsonPropertyName("CorpusId")]
    public long? CorpusId { get; set; }
}

public class S2PublicationVenue
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("issn")]
    public string? Issn { get; set; }
}

public record S2Citation(S2Paper Paper, bool IsInfluential);

// ── Internal API Response Wrappers ───────────────────────────────────

internal class S2SearchResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("data")]
    public List<S2Paper> Data { get; set; } = [];
}

internal class S2CitationResponse
{
    [JsonPropertyName("data")]
    public List<S2CitationEntry>? Data { get; set; }
}

internal class S2CitationEntry
{
    [JsonPropertyName("citingPaper")]
    public S2Paper? CitingPaper { get; set; }

    [JsonPropertyName("isInfluential")]
    public bool IsInfluential { get; set; }
}

internal class S2ReferenceResponse
{
    [JsonPropertyName("data")]
    public List<S2ReferenceEntry>? Data { get; set; }
}

internal class S2ReferenceEntry
{
    [JsonPropertyName("citedPaper")]
    public S2Paper? CitedPaper { get; set; }

    [JsonPropertyName("isInfluential")]
    public bool IsInfluential { get; set; }
}

internal static class S2JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
