using DoomSummarizer.Helpers;

namespace DoomSummarizer.Models;

/// <summary>
/// A node in a hierarchical document tree produced by <see cref="Plugins.IDocumentSplitter"/>.
/// The tree represents the structural decomposition of a document:
/// Root → Parts/Works → Chapters/Acts → Sections/Scenes → Passages.
/// </summary>
public record DocumentNode
{
    /// <summary>Title of this node (e.g. "Chapter 1: The Beginning", "Act III").</summary>
    public required string Title { get; init; }

    /// <summary>Zero-based position among siblings (reading order).</summary>
    public required int Sequence { get; init; }

    /// <summary>Depth level: 0 = root, 1 = part/work, 2 = chapter/act, 3 = section/scene, etc.</summary>
    public required int Level { get; init; }

    /// <summary>Human-readable label for this level: "work", "part", "chapter", "act", "section", "scene", "passage".</summary>
    public required string LevelLabel { get; init; }

    /// <summary>Content text. Only leaf nodes have content; branch nodes are structural.</summary>
    public string? Content { get; init; }

    /// <summary>Child nodes in reading order.</summary>
    public IReadOnlyList<DocumentNode> Children { get; init; } = [];

    /// <summary>Arbitrary metadata (e.g. "author", "date", "characters").</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>True if this is a leaf node (has content, no children).</summary>
    public bool IsLeaf => Children.Count == 0;

    /// <summary>Word count of this node's content (0 for branch nodes). Zero-allocation.</summary>
    public int WordCount => WordCounter.Count(Content);

    /// <summary>
    /// Total word count of this subtree (this node + all descendants).
    /// </summary>
    public int TotalWordCount => WordCount + Children.Sum(c => c.TotalWordCount);

    /// <summary>
    /// Total number of leaf nodes in this subtree.
    /// </summary>
    public int LeafCount => IsLeaf ? 1 : Children.Sum(c => c.LeafCount);

    /// <summary>
    /// Enumerate all leaf nodes in reading order (depth-first).
    /// </summary>
    public IEnumerable<DocumentNode> EnumerateLeaves()
    {
        if (IsLeaf)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
        foreach (var leaf in child.EnumerateLeaves())
            yield return leaf;
    }

    /// <summary>
    /// Enumerate all nodes in reading order (depth-first, pre-order).
    /// </summary>
    public IEnumerable<DocumentNode> EnumerateAll()
    {
        yield return this;
        foreach (var child in Children)
        foreach (var node in child.EnumerateAll())
            yield return node;
    }

    /// <summary>
    /// Build a breadcrumb path from root to this node (e.g. "Hamlet > Act I > Scene 1").
    /// This requires the caller to track the path; this helper builds from a provided ancestry.
    /// </summary>
    public static string BuildBreadcrumb(IEnumerable<DocumentNode> ancestry)
        => string.Join(" > ", ancestry.Select(n => n.Title));
}
