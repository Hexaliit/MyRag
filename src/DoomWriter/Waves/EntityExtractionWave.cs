using System.Text.RegularExpressions;
using DoomSummarizer.Services;
using DoomWriter.Models;
using Mostlylucid.Summarizer.Core.Analysis;

namespace DoomWriter.Waves;

/// <summary>
///     Extracts entities using ONNX BERT-NER (PER, ORG, LOC, MISC) with regex fallback.
///     ML lane — ONNX inference is GPU-bound, limit concurrency.
///     Lower priority — runs after segments are extracted.
/// </summary>
public sealed partial class EntityExtractionWave : ITypedAnalysisWave<string>
{
    private readonly NerService _nerService;
    private bool _nerModelAvailable;
    private bool _nerModelChecked;

    public EntityExtractionWave(NerService nerService)
    {
        _nerService = nerService;
    }

    public string Name => "doc.entities";
    public string Description => "Extract entities via ONNX NER with regex fallback";
    public int Priority => 60;
    public IReadOnlyList<string> Tags => ["ml", SignalTags.Entity];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(string content, AnalysisContext context)
    {
        return !string.IsNullOrWhiteSpace(content) && context.HasCached("segments");
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string markdown, AnalysisContext context, CancellationToken ct = default)
    {
        var segments = context.GetCached<List<AnalyzedSegment>>("segments") ?? [];

        // Check model availability once
        if (!_nerModelChecked)
        {
            _nerModelChecked = true;
            _nerModelAvailable = _nerService.IsAvailable;

            if (!_nerModelAvailable)
                // Start download in background — regex fallback for now
                _ = Task.Run(async () =>
                {
                    await _nerService.EnsureModelAsync(null, CancellationToken.None);
                    _nerModelAvailable = _nerService.IsAvailable;
                }, CancellationToken.None);
        }

        List<TrackedEntity> entities;
        double confidence;

        if (_nerModelAvailable)
        {
            try
            {
                entities = await ExtractEntitiesWithNer(segments, ct);
                confidence = 0.9; // ONNX NER is high confidence
            }
            catch
            {
                entities = ExtractEntitiesBasic(segments);
                confidence = 0.5; // Regex fallback is lower confidence
            }
        }
        else
        {
            entities = ExtractEntitiesBasic(segments);
            confidence = 0.5;
        }

        // Cache entities for downstream waves
        context.SetCached("entities", entities);

        var signals = new List<Signal>
        {
            new()
            {
                Key = "doc.entities.tracked",
                Value = entities,
                Confidence = confidence,
                Source = Name,
                Tags = [SignalTags.Entity],
                Metadata = new Dictionary<string, object>
                {
                    ["extraction_method"] = _nerModelAvailable ? "onnx_ner" : "regex_fallback"
                }
            },
            new()
            {
                Key = "doc.entities.count",
                Value = entities.Count,
                Confidence = confidence,
                Source = Name,
                Tags = [SignalTags.Entity]
            }
        };

        return signals;
    }

    private async Task<List<TrackedEntity>> ExtractEntitiesWithNer(
        List<AnalyzedSegment> segments, CancellationToken ct)
    {
        var entityMentions = new Dictionary<string, (string Type, int Count, List<int> Sections, float MaxConfidence)>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < segments.Count; i++)
        {
            var nerEntities = await _nerService.ExtractEntitiesAsync(segments[i].Text, ct);

            foreach (var ne in nerEntities)
            {
                if (ne.Text.Length < 2) continue;

                if (entityMentions.TryGetValue(ne.Text, out var existing))
                {
                    existing.Count++;
                    if (!existing.Sections.Contains(i))
                        existing.Sections.Add(i);
                    var maxConf = Math.Max(existing.MaxConfidence, (float)ne.Confidence);
                    entityMentions[ne.Text] = (existing.Type, existing.Count, existing.Sections, maxConf);
                }
                else
                {
                    entityMentions[ne.Text] = (ne.Type, 1, [i], (float)ne.Confidence);
                }
            }

            // Backtick code references (NER won't catch these)
            foreach (Match m in BacktickRegex().Matches(segments[i].Text))
            {
                var code = m.Groups[1].Value;
                if (code.Length < 2) continue;
                if (entityMentions.TryGetValue(code, out var existing))
                {
                    existing.Count++;
                    if (!existing.Sections.Contains(i))
                        existing.Sections.Add(i);
                    entityMentions[code] = existing;
                }
                else
                {
                    entityMentions[code] = ("MISC", 1, [i], 1.0f);
                }
            }
        }

        return entityMentions.Select(kv => new TrackedEntity
        {
            Name = kv.Key,
            Type = kv.Value.Type,
            MentionCount = kv.Value.Count,
            SectionIndices = kv.Value.Sections,
            IdfWeight = kv.Value.MaxConfidence
        }).ToList();
    }

    private static List<TrackedEntity> ExtractEntitiesBasic(List<AnalyzedSegment> segments)
    {
        var entityMentions = new Dictionary<string, (string Type, int Count, List<int> Sections)>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < segments.Count; i++)
            foreach (var name in segments[i].EntityNames)
                if (entityMentions.TryGetValue(name, out var existing))
                {
                    existing.Count++;
                    if (!existing.Sections.Contains(i))
                        existing.Sections.Add(i);
                    entityMentions[name] = existing;
                }
                else
                {
                    entityMentions[name] = (InferEntityType(name), 1, [i]);
                }

        return entityMentions.Select(kv => new TrackedEntity
        {
            Name = kv.Key,
            Type = kv.Value.Type,
            MentionCount = kv.Value.Count,
            SectionIndices = kv.Value.Sections
        }).ToList();
    }

    private static string InferEntityType(string name)
    {
        if (name.Contains('.') || name.All(c => char.IsLetterOrDigit(c)))
            return "MISC";
        if (name.EndsWith("Service") || name.EndsWith("API") || name.EndsWith("Inc") || name.EndsWith("Corp"))
            return "ORG";
        return "MISC";
    }

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex BacktickRegex();
}