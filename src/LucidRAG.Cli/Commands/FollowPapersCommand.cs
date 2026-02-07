using System.CommandLine;
using System.Xml.Linq;
using DoomSummarizer.Services;
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
public static class FollowPapersCommand
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
                    var citations = AcademicPatterns.ExtractCitationIds(textToScan);
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
        var arxivUrlMatch = AcademicPatterns.ArxivUrlRegex().Match(source);
        if (arxivUrlMatch.Success)
            return (arxivUrlMatch.Groups[1].Value, "arxiv");

        // DOI
        if (AcademicPatterns.DoiBareIdRegex().IsMatch(source))
            return (source, "doi");

        // DOI URL
        if (source.StartsWith("https://doi.org/", StringComparison.OrdinalIgnoreCase))
            return (source[16..], "doi");

        // Bare arXiv ID
        if (AcademicPatterns.ArxivBareIdRegex().IsMatch(source))
            return (source, "arxiv");

        // Local file
        if (File.Exists(source))
            return (source, "file");

        return (null, "unknown");
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
            var cleanId = AcademicPatterns.StripArxivVersion(arxivId);
            var url = $"{AcademicPatterns.ArxivApiBaseUrl}?id_list={cleanId}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            var entry = doc.Descendants(AcademicPatterns.AtomNamespace + "entry").FirstOrDefault();
            if (entry == null) return null;

            var title = entry.Element(AcademicPatterns.AtomNamespace + "title")?.Value?.Trim() ?? "";
            var summary = entry.Element(AcademicPatterns.AtomNamespace + "summary")?.Value?.Trim() ?? "";
            var authors = entry.Elements(AcademicPatterns.AtomNamespace + "author")
                .Select(a => a.Element(AcademicPatterns.AtomNamespace + "name")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            var published = entry.Element(AcademicPatterns.AtomNamespace + "published")?.Value;
            int? year = DateTimeOffset.TryParse(published, out var pd) ? pd.Year : null;

            var pdfLink = entry.Elements(AcademicPatterns.AtomNamespace + "link")
                .FirstOrDefault(l => l.Attribute("title")?.Value == "pdf")
                ?.Attribute("href")?.Value;

            // Fetch the ar5iv HTML rendering of the full paper — it contains links to all referenced
            // papers (arxiv.org/abs/XXXX links) that the Atom API abstract alone doesn't include.
            // ar5iv.labs.arxiv.org renders LaTeX papers as accessible HTML with hyperlinked references.
            string? fullPageText = null;
            try
            {
                var htmlUrl = $"{AcademicPatterns.Ar5ivBaseUrl}{cleanId}";
                var htmlResponse = await client.GetAsync(htmlUrl, ct);
                if (htmlResponse.IsSuccessStatusCode)
                {
                    fullPageText = await htmlResponse.Content.ReadAsStringAsync(ct);
                    if (verbose)
                        AnsiConsole.MarkupLine(
                            $"  [dim]Fetched ar5iv HTML ({fullPageText.Length:N0} chars) for full-text citation extraction[/]");
                }
                else
                {
                    // Fallback to basic arXiv abstract page
                    htmlUrl = $"https://arxiv.org/abs/{cleanId}";
                    htmlResponse = await client.GetAsync(htmlUrl, ct);
                    if (htmlResponse.IsSuccessStatusCode)
                    {
                        fullPageText = await htmlResponse.Content.ReadAsStringAsync(ct);
                        if (verbose)
                            AnsiConsole.MarkupLine(
                                $"  [dim]Fetched arXiv abstract page ({fullPageText.Length:N0} chars)[/]");
                    }
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"  [dim]Could not fetch HTML page: {Markup.Escape(ex.Message)}[/]");
            }

            // Combine abstract + HTML page text for citation extraction
            var combinedText = summary + "\n" + (fullPageText ?? "");

            return new PaperInfo(title, authors, year, null, arxivId, summary, combinedText, pdfLink);
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
            var url = $"{AcademicPatterns.CrossRefApiBaseUrl}{Uri.EscapeDataString(doi)}";
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

            // CrossRef includes a "reference" array with DOIs of cited papers
            // Build text containing all referenced DOIs for citation extraction
            var referenceDois = new List<string>();
            if (work.TryGetProperty("reference", out var refs))
            {
                foreach (var refEntry in refs.EnumerateArray())
                {
                    if (refEntry.TryGetProperty("DOI", out var refDoi))
                    {
                        var doiVal = refDoi.GetString();
                        if (!string.IsNullOrEmpty(doiVal))
                            referenceDois.Add(doiVal);
                    }
                }
            }

            // Combine abstract + reference DOIs for citation extraction
            var fullText = (abstractText ?? "") + "\n" +
                           string.Join("\n", referenceDois.Select(d => $"10.{d.Split("10.").LastOrDefault()}"));

            if (verbose && referenceDois.Count > 0)
                AnsiConsole.MarkupLine(
                    $"  [dim]CrossRef returned {referenceDois.Count} reference DOI(s)[/]");

            return new PaperInfo(title, authors, year, doi, null, abstractText, fullText, null);
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
