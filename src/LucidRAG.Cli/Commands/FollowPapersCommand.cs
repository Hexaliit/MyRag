using System.CommandLine;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
///     Follow academic paper citations. Fetches a paper, extracts DOIs/arXiv IDs,
///     resolves them via CrossRef/arXiv APIs, and optionally imports cited papers.
/// </summary>
public static partial class FollowPapersCommand
{
    private static readonly Argument<string> SourceArg = new("source")
        { Description = "arXiv URL, DOI, or local PDF/text file path" };

    private static readonly Option<int> DepthOpt = new("--depth", "-d")
        { Description = "Citation levels to follow (default: 1)", DefaultValueFactory = _ => 1 };

    private static readonly Option<int> MaxPapersOpt = new("--max-papers", "-m")
        { Description = "Max total papers to import (default: 10)", DefaultValueFactory = _ => 10 };

    private static readonly Option<string?> CollectionOpt = new("--collection", "-c")
        { Description = "Collection name to store papers in" };

    private static readonly Option<bool> DryRunOpt = new("--dry-run")
        { Description = "Show what would be fetched without importing", DefaultValueFactory = _ => false };

    private static readonly Option<bool> VerboseOpt = new("--verbose", "-v")
        { Description = "Verbose output", DefaultValueFactory = _ => false };

    // arXiv URL pattern: https://arxiv.org/abs/2301.12345
    [GeneratedRegex(@"arxiv\.org/(?:abs|pdf)/(\d{4}\.\d{4,5}(?:v\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ArxivUrlRegex();

    // DOI pattern: 10.xxxx/yyyy
    [GeneratedRegex(@"^10\.\d{4,}/\S+$")]
    private static partial Regex DoiRegex();

    // arXiv ID pattern: 2301.12345
    [GeneratedRegex(@"^\d{4}\.\d{4,5}(?:v\d+)?$")]
    private static partial Regex ArxivIdRegex();

    // Extract DOIs from text
    [GeneratedRegex(@"(?:https?://doi\.org/|doi:\s*)?10\.\d{4,}/[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex DoiInTextRegex();

    // Extract arXiv IDs from text
    [GeneratedRegex(@"arXiv:\s*(\d{4}\.\d{4,5}(?:v\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ArxivInTextRegex();

    // Atom namespace for arXiv API
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public static Command Create()
    {
        var command = new Command("follow-papers", "Follow academic paper citations via arXiv/CrossRef");
        command.Arguments.Add(SourceArg);
        command.Options.Add(DepthOpt);
        command.Options.Add(MaxPapersOpt);
        command.Options.Add(CollectionOpt);
        command.Options.Add(DryRunOpt);
        command.Options.Add(VerboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(SourceArg) ?? "";
            var depth = parseResult.GetValue(DepthOpt);
            var maxPapers = parseResult.GetValue(MaxPapersOpt);
            var collectionName = parseResult.GetValue(CollectionOpt);
            var dryRun = parseResult.GetValue(DryRunOpt);
            var verbose = parseResult.GetValue(VerboseOpt);

            AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Academic Paper Citation Follower[/]\n");

            if (dryRun)
                AnsiConsole.MarkupLine("[yellow]DRY RUN[/] — showing what would be fetched\n");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "LucidRAG/1.0 (https://github.com/scottgal/lucidrag; mailto:scott@scottgal.com)");
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string id, string type, int currentDepth)>();
            var totalFetched = 0;

            // Determine source type
            var (sourceId, sourceType) = ClassifySource(source);
            if (sourceId == null)
            {
                AnsiConsole.MarkupLine($"[red]Cannot classify source:[/] {source}");
                AnsiConsole.MarkupLine("[dim]Supported: arXiv URL, DOI, arXiv ID, or local file path[/]");
                return 1;
            }

            AnsiConsole.MarkupLine($"[cyan]Source:[/] {sourceType} = {sourceId}");
            AnsiConsole.MarkupLine($"[cyan]Depth:[/] {depth}, [cyan]Max papers:[/] {maxPapers}\n");

            queue.Enqueue((sourceId, sourceType, 0));
            seen.Add(sourceId);

            // Set up services for import (only if not dry run)
            ServiceProvider? services = null;
            CliDocumentProcessor? processor = null;
            RagDocumentsDbContext? db = null;
            Guid? collectionId = null;

            if (!dryRun)
            {
                var config = new CliConfig
                {
                    DataDirectory = Program.EnsureDataDirectory(),
                    Verbose = verbose
                };

                services = CliServiceRegistration.BuildServiceProvider(config, verbose);
                await CliServiceRegistration.EnsureDatabaseAsync(services);

                var scope = services.CreateScope();
                db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
                processor = scope.ServiceProvider.GetRequiredService<CliDocumentProcessor>();

                if (!string.IsNullOrEmpty(collectionName))
                {
                    var collection = db.Collections.FirstOrDefault(c => c.Name == collectionName);
                    if (collection == null)
                    {
                        collection = new CollectionEntity { Id = Guid.NewGuid(), Name = collectionName };
                        db.Collections.Add(collection);
                        await db.SaveChangesAsync(ct);
                        AnsiConsole.MarkupLine($"[green]Created collection:[/] {collectionName}");
                    }

                    collectionId = collection.Id;
                }
            }

            // Breadth-first citation following
            while (queue.Count > 0 && totalFetched < maxPapers)
            {
                var (id, type, currentDepth) = queue.Dequeue();

                var paperInfo = await ResolvePaperAsync(httpClient, id, type, verbose, ct);
                if (paperInfo == null)
                {
                    AnsiConsole.MarkupLine($"  [red]Could not resolve:[/] {type}:{id}");
                    continue;
                }

                totalFetched++;
                var depthLabel = currentDepth == 0 ? "[green]SOURCE[/]" : $"[dim]depth {currentDepth}[/]";
                AnsiConsole.MarkupLine(
                    $"\n{depthLabel} [{(currentDepth == 0 ? "green" : "cyan")}]{Markup.Escape(paperInfo.Title)}[/]");
                if (paperInfo.Authors.Count > 0)
                    AnsiConsole.MarkupLine(
                        $"  Authors: {Markup.Escape(string.Join(", ", paperInfo.Authors.Take(5)))}");
                if (paperInfo.Year.HasValue)
                    AnsiConsole.MarkupLine($"  Year: {paperInfo.Year}");
                if (paperInfo.Doi != null)
                    AnsiConsole.MarkupLine($"  DOI: {Markup.Escape(paperInfo.Doi)}");
                if (paperInfo.ArxivId != null)
                    AnsiConsole.MarkupLine($"  arXiv: {Markup.Escape(paperInfo.ArxivId)}");

                // If we should go deeper, extract citations
                if (currentDepth < depth && totalFetched < maxPapers)
                {
                    var textToScan = (paperInfo.Abstract ?? "") + "\n" + (paperInfo.FullText ?? "");
                    var citations = ExtractCitationIds(textToScan);
                    var newCitations = citations.Where(c => seen.Add(c.id)).ToList();

                    if (newCitations.Count > 0)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [dim]Found {newCitations.Count} new citation(s) to follow[/]");

                        foreach (var (citId, citType) in newCitations)
                        {
                            if (totalFetched + queue.Count >= maxPapers) break;
                            queue.Enqueue((citId, citType, currentDepth + 1));
                        }
                    }
                }

                // Rate limiting
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Done![/] Resolved {totalFetched} paper(s).");

            if (dryRun)
                AnsiConsole.MarkupLine("[yellow]Dry run complete — no papers were imported.[/]");

            if (services != null) await services.DisposeAsync();

            return 0;
        });

        return command;
    }

    private static (string? id, string type) ClassifySource(string source)
    {
        // arXiv URL
        var arxivUrlMatch = ArxivUrlRegex().Match(source);
        if (arxivUrlMatch.Success)
            return (arxivUrlMatch.Groups[1].Value, "arxiv");

        // DOI
        if (DoiRegex().IsMatch(source))
            return (source, "doi");

        // DOI URL
        if (source.StartsWith("https://doi.org/", StringComparison.OrdinalIgnoreCase))
            return (source[16..], "doi");

        // Bare arXiv ID
        if (ArxivIdRegex().IsMatch(source))
            return (source, "arxiv");

        // Local file
        if (File.Exists(source))
            return (source, "file");

        return (null, "unknown");
    }

    private static List<(string id, string type)> ExtractCitationIds(string text)
    {
        var results = new List<(string id, string type)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract DOIs
        foreach (Match match in DoiInTextRegex().Matches(text))
        {
            var doi = NormalizeDoi(match.Value);
            if (!string.IsNullOrEmpty(doi) && seen.Add($"doi:{doi}"))
                results.Add((doi, "doi"));
        }

        // Extract arXiv IDs
        foreach (Match match in ArxivInTextRegex().Matches(text))
        {
            var arxivId = match.Groups[1].Value;
            if (seen.Add($"arxiv:{arxivId}"))
                results.Add((arxivId, "arxiv"));
        }

        return results;
    }

    private static string NormalizeDoi(string raw)
    {
        var doi = raw;
        if (doi.StartsWith("doi:", StringComparison.OrdinalIgnoreCase))
            doi = doi[4..].TrimStart();
        if (doi.StartsWith("https://doi.org/", StringComparison.OrdinalIgnoreCase))
            doi = doi[16..];
        if (doi.StartsWith("http://doi.org/", StringComparison.OrdinalIgnoreCase))
            doi = doi[15..];
        if (!doi.StartsWith("10.")) return "";
        doi = doi.TrimEnd('.', ',', ';', ')', ']');
        return doi;
    }

    private static async Task<PaperInfo?> ResolvePaperAsync(
        HttpClient client, string id, string type, bool verbose, CancellationToken ct)
    {
        return type switch
        {
            "arxiv" => await ResolveArxivAsync(client, id, verbose, ct),
            "doi" => await ResolveCrossRefAsync(client, id, verbose, ct),
            "file" => ResolveLocalFile(id),
            _ => null
        };
    }

    private static async Task<PaperInfo?> ResolveArxivAsync(
        HttpClient client, string arxivId, bool verbose, CancellationToken ct)
    {
        try
        {
            var cleanId = arxivId.Contains('v') ? arxivId[..arxivId.IndexOf('v')] : arxivId;
            var url = $"http://export.arxiv.org/api/query?id_list={cleanId}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            var entry = doc.Descendants(Atom + "entry").FirstOrDefault();
            if (entry == null) return null;

            var title = entry.Element(Atom + "title")?.Value?.Trim() ?? "";
            var summary = entry.Element(Atom + "summary")?.Value?.Trim() ?? "";
            var authors = entry.Elements(Atom + "author")
                .Select(a => a.Element(Atom + "name")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            var published = entry.Element(Atom + "published")?.Value;
            int? year = DateTimeOffset.TryParse(published, out var pd) ? pd.Year : null;

            var pdfLink = entry.Elements(Atom + "link")
                .FirstOrDefault(l => l.Attribute("title")?.Value == "pdf")
                ?.Attribute("href")?.Value;

            return new PaperInfo(title, authors, year, null, arxivId, summary, null, pdfLink);
        }
        catch (Exception ex)
        {
            if (verbose) AnsiConsole.MarkupLine($"  [red]arXiv error:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static async Task<PaperInfo?> ResolveCrossRefAsync(
        HttpClient client, string doi, bool verbose, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.crossref.org/works/{Uri.EscapeDataString(doi)}";
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var work = doc.RootElement.GetProperty("message");

            var title = GetJsonArrayFirstString(work, "title") ?? $"DOI:{doi}";
            var authors = GetCrossRefAuthors(work);

            int? year = null;
            foreach (var field in new[] { "published-print", "published-online", "created" })
            {
                if (!work.TryGetProperty(field, out var dateField)) continue;
                if (!dateField.TryGetProperty("date-parts", out var dateParts)) continue;
                var parts = dateParts.EnumerateArray().FirstOrDefault();
                var yearEl = parts.EnumerateArray().FirstOrDefault();
                if (yearEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    year = yearEl.GetInt32();
                    break;
                }
            }

            var abstractText = work.TryGetProperty("abstract", out var abs) ? abs.GetString() : null;

            return new PaperInfo(title, authors, year, doi, null, abstractText, null, null);
        }
        catch (Exception ex)
        {
            if (verbose) AnsiConsole.MarkupLine($"  [red]CrossRef error:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static PaperInfo? ResolveLocalFile(string path)
    {
        if (!File.Exists(path)) return null;

        var text = File.ReadAllText(path);
        var title = Path.GetFileNameWithoutExtension(path);

        return new PaperInfo(title, [], null, null, null, null, text, null);
    }

    private static string? GetJsonArrayFirstString(System.Text.Json.JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return null;
        return prop.ValueKind == System.Text.Json.JsonValueKind.Array
            ? prop.EnumerateArray().FirstOrDefault().GetString()
            : prop.GetString();
    }

    private static List<string> GetCrossRefAuthors(System.Text.Json.JsonElement work)
    {
        if (!work.TryGetProperty("author", out var authorArray)) return [];
        return authorArray.EnumerateArray()
            .Select(a =>
            {
                var given = a.TryGetProperty("given", out var g) ? g.GetString() : "";
                var family = a.TryGetProperty("family", out var f) ? f.GetString() : "";
                return $"{given} {family}".Trim();
            })
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
    }

    private record PaperInfo(
        string Title,
        List<string> Authors,
        int? Year,
        string? Doi,
        string? ArxivId,
        string? Abstract,
        string? FullText,
        string? PdfUrl);
}
