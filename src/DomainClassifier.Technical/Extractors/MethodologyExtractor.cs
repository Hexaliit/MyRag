using DomainClassifier.Core.Models;

namespace DomainClassifier.Technical.Extractors;

/// <summary>
///     Extracts methodology-related entities: algorithms, datasets, frameworks, and statistical methods.
///     Uses static keyword matching for speed and reliability.
/// </summary>
public static class MethodologyExtractor
{
    private static readonly Dictionary<string, string> Algorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gradient descent"] = "algorithm",
        ["stochastic gradient descent"] = "algorithm",
        ["SGD"] = "algorithm",
        ["Adam"] = "algorithm",
        ["AdamW"] = "algorithm",
        ["BERT"] = "algorithm",
        ["GPT"] = "algorithm",
        ["transformer"] = "algorithm",
        ["attention mechanism"] = "algorithm",
        ["self-attention"] = "algorithm",
        ["random forest"] = "algorithm",
        ["SVM"] = "algorithm",
        ["support vector machine"] = "algorithm",
        ["k-means"] = "algorithm",
        ["LSTM"] = "algorithm",
        ["GAN"] = "algorithm",
        ["diffusion model"] = "algorithm",
        ["reinforcement learning"] = "algorithm",
        ["Q-learning"] = "algorithm",
        ["PPO"] = "algorithm",
        ["neural network"] = "algorithm",
        ["convolutional neural network"] = "algorithm",
        ["CNN"] = "algorithm",
        ["RNN"] = "algorithm",
        ["ResNet"] = "algorithm",
        ["YOLO"] = "algorithm",
        ["decision tree"] = "algorithm",
        ["logistic regression"] = "algorithm",
        ["linear regression"] = "algorithm",
        ["XGBoost"] = "algorithm",
        ["LightGBM"] = "algorithm",
        ["word2vec"] = "algorithm",
        ["GloVe"] = "algorithm",
        ["CLIP"] = "algorithm",
        ["LoRA"] = "algorithm",
        ["fine-tuning"] = "algorithm",
        ["beam search"] = "algorithm",
        ["dropout"] = "algorithm",
        ["batch normalization"] = "algorithm"
    };

    private static readonly Dictionary<string, string> Datasets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ImageNet"] = "dataset",
        ["MNIST"] = "dataset",
        ["CIFAR-10"] = "dataset",
        ["CIFAR-100"] = "dataset",
        ["COCO"] = "dataset",
        ["SQuAD"] = "dataset",
        ["GLUE"] = "dataset",
        ["SuperGLUE"] = "dataset",
        ["WikiText"] = "dataset",
        ["Common Crawl"] = "dataset",
        ["The Pile"] = "dataset",
        ["LAION"] = "dataset",
        ["MS MARCO"] = "dataset",
        ["Natural Questions"] = "dataset",
        ["TriviaQA"] = "dataset",
        ["HumanEval"] = "dataset",
        ["MMLU"] = "dataset",
        ["HellaSwag"] = "dataset",
        ["WMT"] = "dataset",
        ["Penn Treebank"] = "dataset",
        ["OpenWebText"] = "dataset",
        ["BookCorpus"] = "dataset"
    };

    private static readonly Dictionary<string, string> Frameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TensorFlow"] = "framework",
        ["PyTorch"] = "framework",
        ["scikit-learn"] = "framework",
        ["Hugging Face"] = "framework",
        ["ONNX"] = "framework",
        ["JAX"] = "framework",
        ["Keras"] = "framework",
        ["Caffe"] = "framework",
        ["MXNet"] = "framework",
        ["spaCy"] = "framework",
        ["NLTK"] = "framework",
        ["OpenCV"] = "framework",
        ["LangChain"] = "framework",
        ["LlamaIndex"] = "framework",
        ["vLLM"] = "framework",
        ["DeepSpeed"] = "framework",
        ["Megatron"] = "framework",
        ["Triton"] = "framework",
        ["MLflow"] = "framework",
        ["Weights & Biases"] = "framework",
        ["wandb"] = "framework"
    };

    private static readonly Dictionary<string, string> StatisticalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["t-test"] = "statistical_method",
        ["chi-square"] = "statistical_method",
        ["ANOVA"] = "statistical_method",
        ["Bayesian"] = "statistical_method",
        ["Monte Carlo"] = "statistical_method",
        ["cross-validation"] = "statistical_method",
        ["k-fold"] = "statistical_method",
        ["bootstrap"] = "statistical_method",
        ["Mann-Whitney"] = "statistical_method",
        ["Wilcoxon"] = "statistical_method",
        ["Kolmogorov-Smirnov"] = "statistical_method",
        ["Fisher's exact"] = "statistical_method",
        ["Pearson correlation"] = "statistical_method",
        ["Spearman correlation"] = "statistical_method",
        ["principal component analysis"] = "statistical_method",
        ["PCA"] = "statistical_method",
        ["t-SNE"] = "statistical_method",
        ["UMAP"] = "statistical_method"
    };

    // All term dictionaries for iteration
    private static readonly Dictionary<string, string>[] AllTermSets =
        [Algorithms, Datasets, Frameworks, StatisticalMethods];

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();
        if (string.IsNullOrWhiteSpace(text)) return entities;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var termSet in AllTermSets)
        foreach (var (term, entityType) in termSet)
        {
            var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            // Avoid duplicate entities for the same term
            var key = $"{entityType}:{term}";
            if (!seen.Add(key)) continue;

            entities.Add(new DomainEntity(
                Text: term,
                EntityType: entityType,
                DomainId: "technical",
                StartOffset: idx,
                EndOffset: idx + term.Length,
                Confidence: 0.85,
                Metadata: new Dictionary<string, object?>
                {
                    ["category"] = entityType
                }));
        }

        return entities;
    }
}
