namespace LucidRAG.Decomposer.Models;

/// <summary>
///     Registry of concept policies. Built-in defaults + plugin-contributed policies.
///     This is the "programmable substrate" that plugins can extend.
///     Instead of: query → retrieve → summarize
///     You get: query → ground concept(s) → select strategy + lens + renderer
///     → retrieve → synthesize → validate → present
/// </summary>
public class ConceptRegistry
{
    private readonly Dictionary<ConceptType, ConceptPolicy> _policies = new();

    public ConceptRegistry()
    {
        RegisterDefaults();
    }

    /// <summary>
    ///     Get the policy for a concept type. Returns General policy as fallback.
    /// </summary>
    public ConceptPolicy GetPolicy(ConceptType concept)
    {
        return _policies.TryGetValue(concept, out var policy) ? policy : _policies[ConceptType.General];
    }

    /// <summary>
    ///     Register or override a concept policy. Plugins can call this to customize behavior.
    /// </summary>
    public void Register(ConceptPolicy policy)
    {
        _policies[policy.Concept] = policy;
    }

    /// <summary>
    ///     Register a lens for a concept. Lenses modify retrieval + output.
    /// </summary>
    public void AddLens(ConceptType concept, string lens)
    {
        if (_policies.TryGetValue(concept, out var policy))
        {
            var lenses = new List<string>(policy.ActiveLenses) { lens };
            _policies[concept] = policy with { ActiveLenses = lenses };
        }
    }

    private void RegisterDefaults()
    {
        _policies[ConceptType.General] = new ConceptPolicy
        {
            Concept = ConceptType.General,
            FetchBudget = 20,
            MinEvidenceItems = 1,
            ResponseFormat = null
        };

        _policies[ConceptType.Definition] = new ConceptPolicy
        {
            Concept = ConceptType.Definition,
            PreferredSources = ["canonical", "wikipedia", "arxiv"],
            SourceBoosts = new Dictionary<string, double>
            {
                ["wikipedia.org"] = 1.3,
                ["arxiv.org"] = 1.2,
                ["*.edu"] = 1.1
            },
            MinEvidenceItems = 1,
            PreferCorpusFirst = true,
            FetchBudget = 5,
            ResponseSections = ["definition", "aliases", "not_to_be_confused_with", "examples"],
            ResponseFormat = "definition_card"
        };

        _policies[ConceptType.Procedure] = new ConceptPolicy
        {
            Concept = ConceptType.Procedure,
            PreferredSources = ["official_docs", "canonical", "search"],
            SourceBoosts = new Dictionary<string, double>
            {
                ["docs.*"] = 1.4,
                ["learn.microsoft.com"] = 1.3,
                ["*.readthedocs.io"] = 1.2
            },
            MinEvidenceItems = 2,
            PreferCorpusFirst = true,
            FetchBudget = 10,
            ResponseSections = ["steps", "prerequisites", "verification", "common_failure_modes"],
            ResponseFormat = "steps"
        };

        _policies[ConceptType.Entity] = new ConceptPolicy
        {
            Concept = ConceptType.Entity,
            PreferredSources = ["search", "gnews", "wikipedia"],
            Recency = RecencyMode.Moderate,
            EnableGraphEnrichment = true,
            FetchBudget = 15,
            ResponseSections = ["facts_card", "relationships", "timeline", "key_documents"],
            ResponseFormat = "entity_card"
        };

        _policies[ConceptType.Incident] = new ConceptPolicy
        {
            Concept = ConceptType.Incident,
            PreferredSources = ["status_pages", "changelogs", "gnews", "search"],
            Recency = RecencyMode.Strong,
            FetchBudget = 20,
            ResponseSections = ["timeline", "blast_radius", "mitigations", "current_status"],
            ResponseFormat = "timeline",
            RequireCitations = true
        };

        _policies[ConceptType.Decision] = new ConceptPolicy
        {
            Concept = ConceptType.Decision,
            PreferredSources = ["search", "arxiv", "canonical"],
            MinEvidenceItems = 3,
            FetchBudget = 15,
            ResponseSections = ["options_table", "constraints", "recommendation", "risks"],
            ResponseFormat = "comparison_table",
            RequireCitations = true
        };

        _policies[ConceptType.Policy] = new ConceptPolicy
        {
            Concept = ConceptType.Policy,
            PreferredSources = ["canonical", "official_docs"],
            SourceBoosts = new Dictionary<string, double>
            {
                ["*.gov"] = 1.5,
                ["*.org"] = 1.2
            },
            MinEvidenceItems = 3,
            FetchBudget = 10,
            ResponseSections = ["rules", "exceptions", "citations"],
            ResponseFormat = "policy_card",
            RequireCitations = true,
            ActiveLenses = ["citation_required"]
        };

        _policies[ConceptType.Troubleshooting] = new ConceptPolicy
        {
            Concept = ConceptType.Troubleshooting,
            PreferredSources = ["issues", "stackoverflow", "runbooks", "search"],
            SourceBoosts = new Dictionary<string, double>
            {
                ["github.com"] = 1.3,
                ["stackoverflow.com"] = 1.3,
                ["*.readthedocs.io"] = 1.1
            },
            Recency = RecencyMode.Moderate,
            FetchBudget = 15,
            ResponseSections = ["diagnosis_tree", "likely_causes", "fixes", "collect_these_logs"],
            ResponseFormat = "diagnosis"
        };

        _policies[ConceptType.Architecture] = new ConceptPolicy
        {
            Concept = ConceptType.Architecture,
            PreferredSources = ["canonical", "arxiv", "search"],
            EnableGraphEnrichment = true,
            FetchBudget = 15,
            ResponseSections = ["components", "flows", "invariants", "failure_modes"],
            ResponseFormat = "architecture"
        };

        _policies[ConceptType.Comparison] = new ConceptPolicy
        {
            Concept = ConceptType.Comparison,
            PreferredSources = ["search", "arxiv", "canonical"],
            MinEvidenceItems = 3,
            FetchBudget = 20,
            ResponseSections = ["side_by_side", "strengths", "weaknesses", "recommendation"],
            ResponseFormat = "comparison_table",
            EnableGraphEnrichment = true
        };

        _policies[ConceptType.Timeline] = new ConceptPolicy
        {
            Concept = ConceptType.Timeline,
            PreferredSources = ["search", "gnews", "wikipedia"],
            Recency = RecencyMode.Moderate,
            FetchBudget = 20,
            ResponseSections = ["chronological_events", "key_milestones", "current_state"],
            ResponseFormat = "timeline"
        };

        _policies[ConceptType.Roundup] = new ConceptPolicy
        {
            Concept = ConceptType.Roundup,
            PreferredSources = ["gnews", "hn", "reddit", "bbc", "search"],
            Recency = RecencyMode.Strong,
            FetchBudget = 30,
            ResponseSections = ["headlines", "analysis", "trends"],
            ResponseFormat = "roundup"
        };
    }
}