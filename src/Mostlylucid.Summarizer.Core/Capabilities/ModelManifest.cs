using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
///     Well-known model IDs - use these constants instead of magic strings.
/// </summary>
public static class ModelIds
{
    // Embedding models
    public const string ClipVitB32 = "clip-vit-b32";
    public const string AllMiniLmL6V2 = "all-minilm-l6-v2";

    // Speech models
    public const string WhisperBase = "whisper-base";

    // Audio models
    public const string EcapaTdnn = "ecapa-tdnn";
    public const string HtDemucs = "htdemucs"; // Source separation (vocals, drums, bass, other)

    // NER models
    public const string BertBaseNer = "bert-base-ner";

    // OCR models
    public const string PaddleOcrDet = "paddleocr-det";
    public const string PaddleOcrRec = "paddleocr-rec";
    public const string TrOcrBase = "trocr-base";

    // Vision models
    public const string Florence2Base = "florence-2-base";
}

/// <summary>
///     Well-known component IDs - use these constants for component registration.
/// </summary>
public static class ComponentIds
{
    // Video waves
    public const string ClipEmbeddingWave = "ClipEmbeddingWave";
    public const string TranscriptionWave = "TranscriptionWave";
    public const string SpeakerDiarizationWave = "SpeakerDiarizationWave";
    public const string TextOcrWave = "TextOcrWave";
    public const string ImageAnalysisWave = "ImageAnalysisWave";
    public const string VisionLlmWave = "VisionLLMWave";

    // Services
    public const string EmbeddingService = "EmbeddingService";
    public const string OnnxNerService = "OnnxNerService";
    public const string BatchClipEmbeddingService = "BatchClipEmbeddingService";
    public const string WhisperService = "WhisperService";
    public const string SpeakerEmbeddingService = "SpeakerEmbeddingService";
    public const string SourceSeparationWave = "SourceSeparationWave"; // HTDemucs for music

    // Contributors
    public const string Florence2Contributor = "Florence2Contributor";
}

/// <summary>
///     Defines a model that can be downloaded and used by the system.
///     Models are lazily downloaded when first needed.
/// </summary>
public record ModelDefinition
{
    /// <summary>
    ///     Unique identifier for the model (e.g., "clip-vit-b32", "whisper-base").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Human-readable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Model category for grouping.
    /// </summary>
    public required ModelCategory Category { get; init; }

    /// <summary>
    ///     URL to download the model from (HuggingFace, direct URL, etc.).
    /// </summary>
    public required string DownloadUrl { get; init; }

    /// <summary>
    ///     Expected file size in bytes (for progress reporting).
    /// </summary>
    public long? ExpectedSizeBytes { get; init; }

    /// <summary>
    ///     SHA256 hash for verification.
    /// </summary>
    public string? Sha256Hash { get; init; }

    /// <summary>
    ///     Relative path within the models directory.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    ///     Minimum execution provider required (null = any).
    /// </summary>
    public string? RequiredProvider { get; init; }

    /// <summary>
    ///     Preferred execution providers in order.
    /// </summary>
    public List<string> PreferredProviders { get; init; } =
        ["CUDAExecutionProvider", "DmlExecutionProvider", "CPUExecutionProvider"];

    /// <summary>
    ///     Components/waves that require this model.
    /// </summary>
    public List<string> RequiredBy { get; init; } = [];

    /// <summary>
    ///     Memory required to load the model (approximate).
    /// </summary>
    public long? MemoryRequiredBytes { get; init; }

    /// <summary>
    ///     Whether this model has a GPU-optimized variant.
    /// </summary>
    public bool HasGpuVariant { get; init; }

    /// <summary>
    ///     URL for GPU-optimized variant (if different).
    /// </summary>
    public string? GpuVariantUrl { get; init; }

    /// <summary>
    ///     Additional files (tokenizer, vocab, etc.)
    /// </summary>
    public Dictionary<string, string>? AdditionalFiles { get; init; }
}

/// <summary>
///     Model categories.
/// </summary>
public enum ModelCategory
{
    Embedding, // CLIP, sentence transformers
    Vision, // Vision LLM, object detection
    Speech, // Whisper, speaker diarization
    Ner, // Named entity recognition
    Ocr, // Text detection, recognition
    Audio, // Audio classification, speaker embedding
    Language // LLM, text generation
}

/// <summary>
///     Component registration with model requirements.
/// </summary>
public record ComponentDefinition
{
    public required string Id { get; init; }
    public List<string> Models { get; init; } = [];
    public List<string> OptionalModels { get; init; } = [];
    public List<string> FallbackChain { get; init; } = [];
}

/// <summary>
///     Central manifest of all models used by the system.
///     Loads from YAML configuration with type-safe constants for model IDs.
/// </summary>
public class ModelManifest
{
    private static ModelManifest? _instance;
    private static readonly object _lock = new();
    private readonly Dictionary<string, ComponentDefinition> _components = new();

    private readonly Dictionary<string, ModelDefinition> _models = new();

    /// <summary>
    ///     Get the singleton instance. Loads from embedded defaults if no YAML provided.
    /// </summary>
    public static ModelManifest Instance
    {
        get
        {
            if (_instance != null) return _instance;

            lock (_lock)
            {
                _instance ??= CreateDefaultManifest();
                return _instance;
            }
        }
    }

    /// <summary>
    ///     All known models.
    /// </summary>
    public IReadOnlyDictionary<string, ModelDefinition> Models => _models;

    /// <summary>
    ///     All registered components.
    /// </summary>
    public IReadOnlyDictionary<string, ComponentDefinition> Components => _components;

    /// <summary>
    ///     Models directory.
    /// </summary>
    public string ModelsDirectory { get; private set; } = GetDefaultModelsDirectory();

    /// <summary>
    ///     Load manifest from YAML file.
    /// </summary>
    public static ModelManifest LoadFromYaml(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        return LoadFromYamlContent(yaml);
    }

    /// <summary>
    ///     Load manifest from YAML content.
    /// </summary>
    public static ModelManifest LoadFromYamlContent(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var yamlDoc = deserializer.Deserialize<YamlManifestDocument>(yamlContent);

        var manifest = new ModelManifest();

        // Parse settings
        if (yamlDoc.Settings != null)
            manifest.ModelsDirectory = ExpandEnvironmentVariables(
                yamlDoc.Settings.ModelsDirectory ?? GetDefaultModelsDirectory());

        // Parse models
        if (yamlDoc.Models != null)
            foreach (var (id, model) in yamlDoc.Models)
            {
                var category = ParseCategory(model.Category);
                manifest._models[id] = new ModelDefinition
                {
                    Id = id,
                    Name = model.Name ?? id,
                    Category = category,
                    DownloadUrl = model.DownloadUrl ?? "",
                    RelativePath = model.RelativePath ?? $"{id}/model.onnx",
                    ExpectedSizeBytes = model.ExpectedSizeBytes,
                    MemoryRequiredBytes = model.MemoryRequiredBytes,
                    HasGpuVariant = model.HasGpuVariant,
                    RequiredBy = model.RequiredBy ?? [],
                    PreferredProviders = model.PreferredProviders ?? ["CPUExecutionProvider"],
                    AdditionalFiles = ParseAdditionalFiles(model)
                };
            }

        // Parse components
        if (yamlDoc.Components != null)
            foreach (var (id, comp) in yamlDoc.Components)
                manifest._components[id] = new ComponentDefinition
                {
                    Id = id,
                    Models = comp.Models ?? [],
                    OptionalModels = comp.OptionalModels ?? [],
                    FallbackChain = comp.FallbackChain ?? []
                };

        return manifest;
    }

    /// <summary>
    ///     Get models required by a specific component.
    /// </summary>
    public IEnumerable<ModelDefinition> GetModelsForComponent(string componentName)
    {
        // Check explicit component registration
        if (_components.TryGetValue(componentName, out var comp))
            foreach (var modelId in comp.Models)
                if (_models.TryGetValue(modelId, out var model))
                    yield return model;

        // Also check RequiredBy in models
        foreach (var model in _models.Values)
            if (model.RequiredBy.Contains(componentName, StringComparer.OrdinalIgnoreCase))
                yield return model;
    }

    /// <summary>
    ///     Get models by category.
    /// </summary>
    public IEnumerable<ModelDefinition> GetModelsByCategory(ModelCategory category)
    {
        return _models.Values.Where(m => m.Category == category);
    }

    /// <summary>
    ///     Get a model by ID.
    /// </summary>
    public ModelDefinition? GetModel(string modelId)
    {
        return _models.GetValueOrDefault(modelId);
    }

    /// <summary>
    ///     Get component definition by ID.
    /// </summary>
    public ComponentDefinition? GetComponent(string componentId)
    {
        return _components.GetValueOrDefault(componentId);
    }

    /// <summary>
    ///     Create default manifest with embedded model definitions.
    /// </summary>
    private static ModelManifest CreateDefaultManifest()
    {
        var manifest = new ModelManifest();

        // CLIP Embeddings
        manifest._models[ModelIds.ClipVitB32] = new ModelDefinition
        {
            Id = ModelIds.ClipVitB32,
            Name = "CLIP ViT-B/32",
            Category = ModelCategory.Embedding,
            DownloadUrl = "https://huggingface.co/openai/clip-vit-base-patch32/resolve/main/onnx/visual_model.onnx",
            RelativePath = "clip/visual_model.onnx",
            ExpectedSizeBytes = 350_000_000,
            MemoryRequiredBytes = 400_000_000,
            HasGpuVariant = true,
            RequiredBy =
                [ComponentIds.ClipEmbeddingWave, ComponentIds.BatchClipEmbeddingService, ComponentIds.ImageAnalysisWave]
        };

        // Sentence Transformer (MiniLM)
        manifest._models[ModelIds.AllMiniLmL6V2] = new ModelDefinition
        {
            Id = ModelIds.AllMiniLmL6V2,
            Name = "all-MiniLM-L6-v2",
            Category = ModelCategory.Embedding,
            DownloadUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx",
            RelativePath = "minilm/model.onnx",
            ExpectedSizeBytes = 90_000_000,
            MemoryRequiredBytes = 120_000_000,
            RequiredBy = [ComponentIds.EmbeddingService]
        };

        // Whisper (Speech-to-Text)
        manifest._models[ModelIds.WhisperBase] = new ModelDefinition
        {
            Id = ModelIds.WhisperBase,
            Name = "Whisper Base",
            Category = ModelCategory.Speech,
            DownloadUrl = "https://huggingface.co/openai/whisper-base/resolve/main/onnx/whisper-base.onnx",
            RelativePath = "whisper/whisper-base.onnx",
            ExpectedSizeBytes = 500_000_000,
            MemoryRequiredBytes = 600_000_000,
            HasGpuVariant = true,
            RequiredBy = [ComponentIds.TranscriptionWave, ComponentIds.WhisperService],
            PreferredProviders = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        };

        // Speaker Embedding (ECAPA-TDNN)
        manifest._models[ModelIds.EcapaTdnn] = new ModelDefinition
        {
            Id = ModelIds.EcapaTdnn,
            Name = "ECAPA-TDNN Speaker Embedding",
            Category = ModelCategory.Audio,
            DownloadUrl = "https://huggingface.co/Wespeaker/wespeaker-ecapa-tdnn512-LM/resolve/main/ecapa_tdnn.onnx",
            RelativePath = "speaker/ecapa_tdnn.onnx",
            ExpectedSizeBytes = 100_000_000,
            MemoryRequiredBytes = 150_000_000,
            RequiredBy = [ComponentIds.SpeakerDiarizationWave, ComponentIds.SpeakerEmbeddingService],
            PreferredProviders = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        };

        // HTDemucs Source Separation (vocals, drums, bass, other)
        manifest._models[ModelIds.HtDemucs] = new ModelDefinition
        {
            Id = ModelIds.HtDemucs,
            Name = "HTDemucs Source Separation",
            Category = ModelCategory.Audio,
            DownloadUrl = "https://huggingface.co/gentij/htdemucs-ort/resolve/main/htdemucs.ort",
            RelativePath = "demucs/htdemucs.ort",
            ExpectedSizeBytes = 220_000_000, // ~210MB
            MemoryRequiredBytes = 3_000_000_000, // ~3GB peak during inference
            RequiredBy = [ComponentIds.SourceSeparationWave],
            PreferredProviders = ["CPUExecutionProvider"] // CPU-optimized ORT format
        };

        // BERT NER
        manifest._models[ModelIds.BertBaseNer] = new ModelDefinition
        {
            Id = ModelIds.BertBaseNer,
            Name = "BERT Base NER",
            Category = ModelCategory.Ner,
            DownloadUrl = "https://huggingface.co/dslim/bert-base-NER/resolve/main/onnx/model.onnx",
            RelativePath = "ner/bert-base-ner.onnx",
            ExpectedSizeBytes = 500_000_000,
            MemoryRequiredBytes = 600_000_000,
            RequiredBy = [ComponentIds.OnnxNerService, ComponentIds.TranscriptionWave],
            PreferredProviders = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        };

        // PaddleOCR Detection
        manifest._models[ModelIds.PaddleOcrDet] = new ModelDefinition
        {
            Id = ModelIds.PaddleOcrDet,
            Name = "PaddleOCR Detection",
            Category = ModelCategory.Ocr,
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PaddleOCR/resolve/main/det_onnx/model.onnx",
            RelativePath = "ocr/paddleocr_det.onnx",
            ExpectedSizeBytes = 4_000_000,
            MemoryRequiredBytes = 50_000_000,
            RequiredBy = [ComponentIds.TextOcrWave]
        };

        // PaddleOCR Recognition
        manifest._models[ModelIds.PaddleOcrRec] = new ModelDefinition
        {
            Id = ModelIds.PaddleOcrRec,
            Name = "PaddleOCR Recognition",
            Category = ModelCategory.Ocr,
            DownloadUrl = "https://huggingface.co/PaddlePaddle/PaddleOCR/resolve/main/rec_onnx/model.onnx",
            RelativePath = "ocr/paddleocr_rec.onnx",
            ExpectedSizeBytes = 10_000_000,
            MemoryRequiredBytes = 80_000_000,
            RequiredBy = [ComponentIds.TextOcrWave]
        };

        // TrOCR
        manifest._models[ModelIds.TrOcrBase] = new ModelDefinition
        {
            Id = ModelIds.TrOcrBase,
            Name = "TrOCR Base",
            Category = ModelCategory.Ocr,
            DownloadUrl = "https://huggingface.co/microsoft/trocr-base-printed/resolve/main/onnx/model.onnx",
            RelativePath = "ocr/trocr_base.onnx",
            ExpectedSizeBytes = 350_000_000,
            MemoryRequiredBytes = 450_000_000,
            HasGpuVariant = true,
            RequiredBy = [ComponentIds.ImageAnalysisWave],
            PreferredProviders = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        };

        // Florence-2
        manifest._models[ModelIds.Florence2Base] = new ModelDefinition
        {
            Id = ModelIds.Florence2Base,
            Name = "Florence-2 Base",
            Category = ModelCategory.Vision,
            DownloadUrl = "https://huggingface.co/microsoft/Florence-2-base/resolve/main/onnx/model.onnx",
            RelativePath = "florence/model.onnx",
            ExpectedSizeBytes = 850_000_000,
            MemoryRequiredBytes = 1_000_000_000,
            HasGpuVariant = true,
            RequiredBy = [ComponentIds.Florence2Contributor, ComponentIds.VisionLlmWave],
            PreferredProviders = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        };

        // Component registrations
        manifest._components[ComponentIds.ClipEmbeddingWave] = new ComponentDefinition
        {
            Id = ComponentIds.ClipEmbeddingWave,
            Models = [ModelIds.ClipVitB32],
            FallbackChain = [ComponentIds.ImageAnalysisWave]
        };

        manifest._components[ComponentIds.TranscriptionWave] = new ComponentDefinition
        {
            Id = ComponentIds.TranscriptionWave,
            Models = [ModelIds.WhisperBase],
            OptionalModels = [ModelIds.BertBaseNer]
        };

        manifest._components[ComponentIds.TextOcrWave] = new ComponentDefinition
        {
            Id = ComponentIds.TextOcrWave,
            Models = [ModelIds.PaddleOcrDet, ModelIds.PaddleOcrRec],
            FallbackChain = [ModelIds.TrOcrBase]
        };

        manifest._components[ComponentIds.SourceSeparationWave] = new ComponentDefinition
        {
            Id = ComponentIds.SourceSeparationWave,
            Models = [ModelIds.HtDemucs]
        };

        return manifest;
    }

    private static ModelCategory ParseCategory(string? category)
    {
        return category?.ToLowerInvariant() switch
        {
            "embedding" => ModelCategory.Embedding,
            "vision" => ModelCategory.Vision,
            "speech" => ModelCategory.Speech,
            "ner" => ModelCategory.Ner,
            "ocr" => ModelCategory.Ocr,
            "audio" => ModelCategory.Audio,
            "language" => ModelCategory.Language,
            _ => ModelCategory.Embedding
        };
    }

    private static Dictionary<string, string>? ParseAdditionalFiles(YamlModelDefinition model)
    {
        var files = new Dictionary<string, string>();

        if (model.Tokenizer != null)
        {
            if (!string.IsNullOrEmpty(model.Tokenizer.VocabUrl))
                files["vocab_url"] = model.Tokenizer.VocabUrl;
            if (!string.IsNullOrEmpty(model.Tokenizer.VocabPath))
                files["vocab_path"] = model.Tokenizer.VocabPath;
        }

        if (model.CharacterDict != null)
        {
            if (!string.IsNullOrEmpty(model.CharacterDict.Url))
                files["char_dict_url"] = model.CharacterDict.Url;
            if (!string.IsNullOrEmpty(model.CharacterDict.Path))
                files["char_dict_path"] = model.CharacterDict.Path;
        }

        return files.Count > 0 ? files : null;
    }

    private static string ExpandEnvironmentVariables(string path)
    {
        // Handle ${VAR:-default} syntax
        var pattern = @"\$\{([^:}]+)(?::-([^}]*))?\}";
        return Regex.Replace(path, pattern, match =>
        {
            var varName = match.Groups[1].Value;
            var defaultValue = match.Groups[2].Success ? match.Groups[2].Value : "";

            var value = Environment.GetEnvironmentVariable(varName);
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        });
    }

    private static string GetDefaultModelsDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "lucidrag", "models");
    }

    // YAML deserialization classes
    private class YamlManifestDocument
    {
        public YamlSettings? Settings { get; set; }
        public Dictionary<string, YamlModelDefinition>? Models { get; set; }
        public Dictionary<string, YamlComponentDefinition>? Components { get; set; }
    }

    private class YamlSettings
    {
        public string? ModelsDirectory { get; set; }
        public int? DownloadTimeoutMinutes { get; set; }
        public bool? VerifyHashes { get; set; }
        public bool? PreferGpu { get; set; }
    }

    private class YamlModelDefinition
    {
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? DownloadUrl { get; set; }
        public string? RelativePath { get; set; }
        public long? ExpectedSizeBytes { get; set; }
        public long? MemoryRequiredBytes { get; set; }
        public bool HasGpuVariant { get; set; }
        public List<string>? RequiredBy { get; set; }
        public List<string>? PreferredProviders { get; set; }
        public YamlTokenizer? Tokenizer { get; set; }
        public YamlCharDict? CharacterDict { get; set; }
    }

    private class YamlTokenizer
    {
        public string? VocabUrl { get; set; }
        public string? VocabPath { get; set; }
    }

    private class YamlCharDict
    {
        public string? Url { get; set; }
        public string? Path { get; set; }
    }

    private class YamlComponentDefinition
    {
        public List<string>? Models { get; set; }
        public List<string>? OptionalModels { get; set; }
        public List<string>? FallbackChain { get; set; }
    }
}