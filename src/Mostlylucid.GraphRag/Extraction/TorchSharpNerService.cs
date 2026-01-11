using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.TorchSharp;

namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// NER service using ML.NET TorchSharp (RoBERTa-based).
/// More reliable than raw ONNX as it's properly integrated with ML.NET pipeline.
///
/// ML.NET 3.0+ provides built-in NER support via TorchSharp bindings.
/// This uses RoBERTa model which is pre-trained for token classification.
/// </summary>
public sealed class TorchSharpNerService : IDisposable
{
    private readonly MLContext _mlContext;
    private readonly string _modelPath;
    private ITransformer? _trainedModel;
    private PredictionEngine<NerInput, NerPrediction>? _predictionEngine;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Standard NER labels (CoNLL-2003 style)
    private static readonly string[] DefaultLabels =
    {
        "O",        // Outside any entity
        "B-PER",    // Beginning of person name
        "I-PER",    // Inside person name
        "B-ORG",    // Beginning of organization
        "I-ORG",    // Inside organization
        "B-LOC",    // Beginning of location
        "I-LOC",    // Inside location
        "B-MISC",   // Beginning of miscellaneous entity
        "I-MISC"    // Inside miscellaneous entity
    };

    public TorchSharpNerService(string? modelPath = null)
    {
        _mlContext = new MLContext(seed: 42);
        _modelPath = modelPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LucidRAG", "models", "torchsharp-ner");

        Directory.CreateDirectory(_modelPath);
    }

    /// <summary>
    /// Initialize the NER model (loads pre-trained or trains a minimal model).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var savedModelPath = Path.Combine(_modelPath, "ner-model.zip");

            if (File.Exists(savedModelPath))
            {
                // Load existing model
                Console.WriteLine("[TorchSharp NER] Loading saved model...");
                _trainedModel = _mlContext.Model.Load(savedModelPath, out _);
            }
            else
            {
                // Train a minimal model with sample data
                Console.WriteLine("[TorchSharp NER] Training new model (this may take a moment)...");
                _trainedModel = await TrainMinimalModelAsync(ct);

                // Save for future use
                _mlContext.Model.Save(_trainedModel, null, savedModelPath);
                Console.WriteLine($"[TorchSharp NER] Model saved to {savedModelPath}");
            }

            // Create prediction engine - ignoreMissingColumns=true because inference doesn't have Labels
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<NerInput, NerPrediction>(
                _trainedModel, ignoreMissingColumns: true);

            _initialized = true;
            Console.WriteLine("[TorchSharp NER] Initialized successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Extract named entities from text.
    /// </summary>
    public async Task<List<EntitySpan>> ExtractAsync(string text, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        if (_predictionEngine == null)
            return new List<EntitySpan>();

        var input = new NerInput { Sentence = text };
        var prediction = _predictionEngine.Predict(input);

        return ParsePrediction(text, prediction);
    }

    /// <summary>
    /// Extract entities with profile-aware type mapping.
    /// </summary>
    public async Task<List<EntityCandidate>> ExtractWithProfileAsync(
        string text,
        EntityProfile profile,
        CancellationToken ct = default)
    {
        var spans = await ExtractAsync(text, ct);

        return spans.Select(s => new EntityCandidate
        {
            Name = s.Text,
            Type = MapToProfileType(s.EntityType, profile),
            Confidence = s.Confidence,
            Signals = ["torchsharp_ner"]
        }).ToList();
    }

    private List<EntitySpan> ParsePrediction(string originalText, NerPrediction prediction)
    {
        var entities = new List<EntitySpan>();

        if (prediction.PredictedLabels == null || prediction.PredictedLabels.Length == 0)
            return entities;

        // Simple word tokenization for alignment
        var words = originalText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        EntitySpan? current = null;
        var currentWords = new List<string>();

        for (int i = 0; i < Math.Min(words.Length, prediction.PredictedLabels.Length); i++)
        {
            var label = prediction.PredictedLabels[i];
            var word = words[i];

            if (label.StartsWith("B-"))
            {
                // Save previous entity
                if (current != null && currentWords.Count > 0)
                {
                    current.Text = string.Join(" ", currentWords);
                    entities.Add(current);
                }

                // Start new entity
                var entityType = label[2..]; // Remove "B-" prefix
                current = new EntitySpan
                {
                    EntityType = entityType,
                    Confidence = 0.85 // Default confidence for TorchSharp
                };
                currentWords = new List<string> { word };
            }
            else if (label.StartsWith("I-") && current != null)
            {
                // Continue current entity
                currentWords.Add(word);
            }
            else if (label == "O")
            {
                // Save and reset
                if (current != null && currentWords.Count > 0)
                {
                    current.Text = string.Join(" ", currentWords);
                    entities.Add(current);
                }
                current = null;
                currentWords.Clear();
            }
        }

        // Don't forget last entity
        if (current != null && currentWords.Count > 0)
        {
            current.Text = string.Join(" ", currentWords);
            entities.Add(current);
        }

        return entities
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .DistinctBy(e => e.Text.ToLowerInvariant())
            .ToList();
    }

    private async Task<ITransformer> TrainMinimalModelAsync(CancellationToken ct)
    {
        // Create minimal training data for NER
        // In production, you'd use a larger dataset like CoNLL-2003
        var trainingData = CreateMinimalTrainingData();

        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        // Define the NER training pipeline
        // ML.NET NER requires: MapValueToKey on labels -> NER trainer -> MapKeyToValue
        var pipeline = _mlContext.Transforms.Conversion.MapValueToKey(
                outputColumnName: "LabelKey",
                inputColumnName: nameof(NerTrainingData.Labels))
            .Append(_mlContext.MulticlassClassification.Trainers.NamedEntityRecognition(
                labelColumnName: "LabelKey",
                outputColumnName: "PredictedLabelKey",
                sentence1ColumnName: nameof(NerTrainingData.Sentence)))
            .Append(_mlContext.Transforms.Conversion.MapKeyToValue(
                outputColumnName: nameof(NerPrediction.PredictedLabels),
                inputColumnName: "PredictedLabelKey"));

        // Train the model
        Console.WriteLine("[TorchSharp NER] Starting training...");
        var model = await Task.Run(() => pipeline.Fit(dataView), ct);
        Console.WriteLine("[TorchSharp NER] Training complete");

        return model;
    }

    private List<NerTrainingData> CreateMinimalTrainingData()
    {
        // Minimal training examples - just enough to get the model working
        // Each example needs sentence and corresponding BIO labels per word
        return new List<NerTrainingData>
        {
            new() {
                Sentence = "John Smith works at Microsoft in Seattle",
                Labels = new[] { "B-PER", "I-PER", "O", "O", "B-ORG", "O", "B-LOC" }
            },
            new() {
                Sentence = "Apple announced new products in San Francisco yesterday",
                Labels = new[] { "B-ORG", "O", "O", "O", "O", "B-LOC", "I-LOC", "O" }
            },
            new() {
                Sentence = "The CEO of Google visited the White House",
                Labels = new[] { "O", "O", "O", "B-ORG", "O", "O", "B-LOC", "I-LOC" }
            },
            new() {
                Sentence = "Scott Galloway wrote about Entity Framework and PostgreSQL",
                Labels = new[] { "B-PER", "I-PER", "O", "O", "B-MISC", "I-MISC", "O", "B-MISC" }
            },
            new() {
                Sentence = "Microsoft Azure and Amazon AWS compete in cloud computing",
                Labels = new[] { "B-ORG", "B-MISC", "O", "B-ORG", "B-MISC", "O", "O", "O", "O" }
            },
            new() {
                Sentence = "The Eiffel Tower is located in Paris France",
                Labels = new[] { "O", "B-LOC", "I-LOC", "O", "O", "O", "B-LOC", "B-LOC" }
            },
            new() {
                Sentence = "OpenAI released ChatGPT in November 2022",
                Labels = new[] { "B-ORG", "O", "B-MISC", "O", "O", "O" }
            },
            new() {
                Sentence = "Dr Jane Watson joined IBM Research last month",
                Labels = new[] { "O", "B-PER", "I-PER", "O", "B-ORG", "I-ORG", "O", "O" }
            }
        };
    }

    private static string MapToProfileType(string nerType, EntityProfile profile)
    {
        var mappedType = nerType.ToUpperInvariant() switch
        {
            "PER" or "PERSON" => FindBestMatch(profile, "person", "party", "individual"),
            "ORG" or "ORGANIZATION" => FindBestMatch(profile, "organization", "company", "party"),
            "LOC" or "LOCATION" => FindBestMatch(profile, "location", "jurisdiction"),
            "MISC" or "MISCELLANEOUS" => FindBestMatch(profile, "concept", "technology", "product"),
            _ => profile.EntityTypes.FirstOrDefault()?.Name ?? "concept"
        };
        return mappedType;
    }

    private static string FindBestMatch(EntityProfile profile, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = profile.EntityTypes.FirstOrDefault(t =>
                t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                t.Aliases.Contains(candidate, StringComparer.OrdinalIgnoreCase));
            if (match != null)
                return match.Name;
        }
        return profile.EntityTypes.FirstOrDefault()?.Name ?? "concept";
    }

    public void Dispose()
    {
        _predictionEngine?.Dispose();
        _initLock.Dispose();
    }
}

/// <summary>
/// Input schema for NER prediction.
/// </summary>
public class NerInput
{
    public string Sentence { get; set; } = "";
}

/// <summary>
/// Output schema for NER prediction.
/// </summary>
public class NerPrediction
{
    [ColumnName("PredictedLabels")]
    public string[] PredictedLabels { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Training data schema for NER.
/// </summary>
public class NerTrainingData
{
    public string Sentence { get; set; } = "";
    public string[] Labels { get; set; } = Array.Empty<string>();
}
