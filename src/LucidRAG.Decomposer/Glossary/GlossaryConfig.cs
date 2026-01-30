namespace LucidRAG.Decomposer.Glossary;

/// <summary>
/// YAML/JSON glossary model. Defines foundational terms that are fetched,
/// summarized, embedded, and stored in KB at startup.
/// </summary>
public class GlossaryConfig
{
    /// <summary>Corpus name (e.g., "ai-fundamentals").</summary>
    public string Corpus { get; set; } = "";

    /// <summary>Schema version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Terms to seed in the knowledge base.</summary>
    public List<GlossaryTerm> Terms { get; set; } = [];
}

public class GlossaryTerm
{
    /// <summary>Term name (e.g., "transformer architecture").</summary>
    public string Term { get; set; } = "";

    /// <summary>Query to execute for seeding (e.g., "What is a transformer?").</summary>
    public string Query { get; set; } = "";

    /// <summary>Preferred sources for this term.</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>Time-to-live in days before re-fetching.</summary>
    public int TtlDays { get; set; } = 30;

    /// <summary>Aliases for this term.</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Concept type this term belongs to.</summary>
    public string? ConceptType { get; set; }
}
