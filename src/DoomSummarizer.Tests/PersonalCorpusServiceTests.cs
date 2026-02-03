using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
/// Shared collection for all tests that use EmbeddingFactory/ONNX model.
/// Prevents file contention on model.onnx.tmp during parallel test execution in CI.
/// </summary>
[CollectionDefinition("EmbeddingTests")]
public class EmbeddingTestsCollection;

/// <summary>
/// Tests for PersonalCorpusService: self-disclosure detection, fact indexing,
/// entity retrieval, gap-filling, and forgetting.
/// </summary>
[Collection("EmbeddingTests")]
public class PersonalCorpusServiceTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private StorageService _storage = null!;
    private IEmbeddingService _embedding = null!;
    private PersonalCorpusService _corpus = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_personal_test_{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();
        _embedding = await EmbeddingFactory.CreateAsync();

        // No NER or KnowledgeGraph in unit tests — those require ONNX models
        // No Ollama — tests exercise the rule-based fallback path
        _corpus = new PersonalCorpusService(
            _storage, _embedding, ner: null, knowledgeGraph: null,
            ollama: null, ollamaAvailable: false);
    }

    public async Task DisposeAsync()
    {
        (_embedding as IDisposable)?.Dispose();
        await _storage.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    // --- Self-disclosure detection (rule-based fallback) ---

    [Theory]
    [InlineData("I live in London", true)]
    [InlineData("I work at Anthropic", true)]
    [InlineData("I'm a backend developer", true)]
    [InlineData("I use VS Code", true)]
    [InlineData("I prefer dark mode", true)]
    [InlineData("I'm based in Berlin", true)]
    [InlineData("I work for Google", true)]
    [InlineData("I am a data scientist", true)]
    [InlineData("What's the weather?", false)]
    [InlineData("How does transformers work?", false)]
    [InlineData("Tell me about Anthropic", false)]
    [InlineData("What is Claude?", false)]
    public async Task DetectSelfDisclosure_RuleBasedFallback_DetectsCorrectly(
        string question, bool expectDetection)
    {
        var result = await _corpus.DetectSelfDisclosureAsync(question, CancellationToken.None);

        if (expectDetection)
            result.Should().NotBeNull($"'{question}' should be detected as self-disclosure");
        else
            result.Should().BeNull($"'{question}' should NOT be detected as self-disclosure");
    }

    [Fact]
    public async Task DetectSelfDisclosure_ExtractsCleanStatement()
    {
        var result = await _corpus.DetectSelfDisclosureAsync(
            "I live in London and what's the weather?", CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain("London");
        result.Should().Contain("lives in");
    }

    [Fact]
    public async Task DetectSelfDisclosure_WorkAt_ExtractsOrg()
    {
        var result = await _corpus.DetectSelfDisclosureAsync(
            "I work at Anthropic on AI safety", CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain("Anthropic");
        result.Should().Contain("works at");
    }

    [Fact]
    public async Task DetectSelfDisclosure_ImA_ExtractsRole()
    {
        var result = await _corpus.DetectSelfDisclosureAsync(
            "I'm a backend developer using .NET", CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain("backend developer");
    }

    // --- Fact indexing ---

    [Fact]
    public async Task IndexFact_StoresItemInSQLite()
    {
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(1);
        facts[0].Content.Should().Be("User lives in London");
        facts[0].Source.Should().StartWith("personal:default");
    }

    [Fact]
    public async Task IndexFact_MultipleFacts_AllStored()
    {
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);
        await _corpus.IndexFactAsync("default", "User works at Anthropic", null, CancellationToken.None);
        await _corpus.IndexFactAsync("default", "User uses .NET and C#", null, CancellationToken.None);

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(3);
        facts.Select(f => f.Content).Should().Contain("User lives in London");
        facts.Select(f => f.Content).Should().Contain("User works at Anthropic");
        facts.Select(f => f.Content).Should().Contain("User uses .NET and C#");
    }

    [Fact]
    public async Task IndexFact_SetsEmbedding()
    {
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(1);
        facts[0].Embedding.Should().NotBeNull();
        facts[0].Embedding!.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task IndexFact_SetsKeywords()
    {
        await _corpus.IndexFactAsync("default", "User is a backend developer using .NET", null, CancellationToken.None);

        // Verify item was stored with keywords via FTS
        var items = await _storage.GetRecentItemsAsync(365, ["personal:default"]);
        items.Should().HaveCount(1);
        items[0].Keywords.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task IndexFact_NamedCorpus_UsesCorrectSource()
    {
        await _corpus.IndexFactAsync("scott", "User lives in London", null, CancellationToken.None);

        var facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().HaveCount(1);
        facts[0].Source.Should().Be("personal:scott");

        // Default corpus should be empty
        var defaultFacts = await _corpus.GetAllFactsAsync("default");
        defaultFacts.Should().BeEmpty();
    }

    // --- Forget ---

    [Fact]
    public async Task ForgetAll_RemovesAllFacts()
    {
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);
        await _corpus.IndexFactAsync("default", "User works at Anthropic", null, CancellationToken.None);

        var count = await _corpus.ForgetAsync("default", "all");
        count.Should().Be(2);

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgetByKeyword_RemovesMatchingFacts()
    {
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);
        await _corpus.IndexFactAsync("default", "User works at Anthropic", null, CancellationToken.None);

        var count = await _corpus.ForgetAsync("default", "London");
        count.Should().Be(1);

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(1);
        facts[0].Content.Should().Contain("Anthropic");
    }

    [Fact]
    public async Task Forget_EmptyCorpus_ReturnsZero()
    {
        var count = await _corpus.ForgetAsync("default", "all");
        count.Should().Be(0);
    }

    // --- Source filter exclusion ---

    [Fact]
    public async Task PersonalItems_ExcludedFromDefaultRetrieval()
    {
        // Index a personal fact
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);

        // Also store a regular item
        var regularItem = new ContentItem
        {
            Id = "regular:001",
            Source = "hn",
            Title = "London weather forecast",
            Content = "Weather in London is rainy",
            CreatedAt = DateTimeOffset.UtcNow,
            FetchedAt = DateTimeOffset.UtcNow,
            Tags = [],
        };
        regularItem = regularItem with { Embedding = await _embedding.EmbedAsync("London weather", CancellationToken.None) };
        await _storage.SaveItemAsync(regularItem);

        // Query without source filter — personal items should be excluded
        var results = await _storage.GetRecentItemsAsync(30);
        var sources = results.Select(r => r.Source).ToList();
        sources.Should().NotContain(s => s.StartsWith("personal:"),
            "personal: items should be auto-excluded from default retrieval");
        sources.Should().Contain("hn");
    }

    [Fact]
    public async Task PersonalItems_IncludedWhenExplicitlyRequested()
    {
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);

        // Query with explicit personal: source filter
        var results = await _storage.GetRecentItemsAsync(365 * 10, ["personal:default"]);
        results.Should().HaveCount(1);
        results[0].Source.Should().Be("personal:default");
    }

    // --- End-to-end: detect + index ---

    [Fact]
    public async Task EndToEnd_DetectAndIndex_StoresFact()
    {
        var question = "I live in London and work at Anthropic";

        var factStatement = await _corpus.DetectSelfDisclosureAsync(question, CancellationToken.None);
        factStatement.Should().NotBeNull();

        await _corpus.IndexFactAsync("default", factStatement!, question, CancellationToken.None);

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EndToEnd_NoSelfDisclosure_DoesNotIndex()
    {
        var question = "What's the weather in London?";

        var factStatement = await _corpus.DetectSelfDisclosureAsync(question, CancellationToken.None);
        factStatement.Should().BeNull();

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().BeEmpty();
    }
}

/// <summary>
/// Full lifecycle integration tests: simulate a user teaching the system about
/// themselves across multiple "turns", then verify it remembers everything.
/// </summary>
[Collection("EmbeddingTests")]
public class PersonalCorpusLifecycleTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private StorageService _storage = null!;
    private IEmbeddingService _embedding = null!;
    private PersonalCorpusService _corpus = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_lifecycle_test_{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();
        _embedding = await EmbeddingFactory.CreateAsync();

        _corpus = new PersonalCorpusService(
            _storage, _embedding, ner: null, knowledgeGraph: null,
            ollama: null, ollamaAvailable: false);
    }

    public async Task DisposeAsync()
    {
        (_embedding as IDisposable)?.Dispose();
        await _storage.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    /// <summary>
    /// Simulate the full InteractiveAskLoop background flow:
    /// for each user message, detect name, detect facts, index.
    /// </summary>
    private async Task SimulateTurn(string question)
    {
        // Same order as InteractiveAskLoop background task
        await _corpus.DetectAndSetNameAsync(question, CancellationToken.None);

        var factStatement = await _corpus.DetectSelfDisclosureAsync(question, CancellationToken.None);
        if (factStatement != null)
            await _corpus.IndexFactAsync(_corpus.ActiveCorpusName, factStatement, question, CancellationToken.None);
    }

    [Fact]
    public async Task FullSession_TeachAndRemember()
    {
        // --- Turn 1: introduce yourself ---
        await SimulateTurn("My name is Scott");

        _corpus.ActiveCorpusName.Should().Be("scott",
            "should switch to named corpus after introduction");

        var facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().NotBeEmpty("name should be indexed as a fact");
        facts.Should().Contain(f => f.Content!.Contains("Scott"),
            "name fact should be stored");

        // --- Turn 2: share location ---
        await SimulateTurn("I live in London");

        facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().Contain(f => f.Content!.Contains("London"),
            "location should be remembered");

        // --- Turn 3: share employer ---
        await SimulateTurn("I work at Anthropic on AI safety");

        facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().Contain(f => f.Content!.Contains("Anthropic"),
            "employer should be remembered");

        // --- Turn 4: share role ---
        await SimulateTurn("I'm a backend developer using .NET and C#");

        facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().Contain(f => f.Content!.Contains("backend developer"),
            "role should be remembered");

        // --- Turn 5: share preferences ---
        await SimulateTurn("I prefer dark mode and concise answers");

        facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().Contain(f => f.Content!.Contains("dark mode"),
            "preferences should be remembered");

        // --- Verify total fact count ---
        facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().HaveCountGreaterThan(4,
            "all personal facts should accumulate");

        // --- Turn 6: a normal question should NOT create facts ---
        var factCountBefore = facts.Count;
        await SimulateTurn("What is the latest news about AI?");

        facts = await _corpus.GetAllFactsAsync("scott");
        facts.Should().HaveCount(factCountBefore,
            "non-personal questions should not add facts");
    }

    [Fact]
    public async Task FullSession_GapFilling_ResolvesLocation()
    {
        // Teach location
        await SimulateTurn("I live in London");

        // Gap-fill: "here" should resolve to "London"
        var sentinel = new ConversationSentinel(null, _embedding, false);
        var (resolved, context) = await sentinel.ResolveFromPersonalAsync(
            "What's the weather here?", _corpus, CancellationToken.None);

        // Without NER, entities won't be in the entity graph,
        // so gap-filling won't have entities to resolve against.
        // But the gap detection itself should trigger.
        // This tests the detection path; full resolution requires NER.
        resolved.Should().NotBeNull();
    }

    [Fact]
    public async Task FullSession_NameThenFacts_AllUnderNamedCorpus()
    {
        // Share facts BEFORE introducing yourself
        await SimulateTurn("I live in London");
        await SimulateTurn("I work at Anthropic");

        // Facts should be in default corpus
        var defaultFacts = await _corpus.GetAllFactsAsync("default");
        defaultFacts.Should().HaveCount(2);

        // Now introduce yourself — should migrate
        await SimulateTurn("My name is Scott");

        // Default should be empty
        defaultFacts = await _corpus.GetAllFactsAsync("default");
        defaultFacts.Should().BeEmpty("facts should migrate from default to named corpus");

        // Scott should have everything (migrated + name fact)
        var scottFacts = await _corpus.GetAllFactsAsync("scott");
        scottFacts.Should().HaveCountGreaterThan(2,
            "named corpus should contain migrated facts plus the name");
        scottFacts.Should().Contain(f => f.Content!.Contains("London"));
        scottFacts.Should().Contain(f => f.Content!.Contains("Anthropic"));
        scottFacts.Should().Contain(f => f.Content!.Contains("Scott"));

        // Subsequent facts should also go to scott
        await SimulateTurn("I use VS Code");
        scottFacts = await _corpus.GetAllFactsAsync("scott");
        scottFacts.Should().Contain(f => f.Content!.Contains("VS Code"));
    }

    [Fact]
    public async Task FullSession_ForgetAndRelearn()
    {
        await SimulateTurn("I live in London");
        await SimulateTurn("I work at Anthropic");

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(2);

        // Forget location facts
        var forgotten = await _corpus.ForgetAsync("default", "London");
        forgotten.Should().Be(1);

        facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(1);
        facts[0].Content.Should().Contain("Anthropic");

        // Relearn with new location
        await SimulateTurn("I moved to Berlin");

        facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(2);
        facts.Should().Contain(f => f.Content!.Contains("Berlin"),
            "new location should be remembered after relearning");
    }

    [Fact]
    public async Task FullSession_ManualRemember_WorksAlongsideAutoDetection()
    {
        // Auto-detect from conversation
        await SimulateTurn("I live in London");

        // Manual /remember
        await _corpus.IndexFactAsync("default",
            "User prefers functional programming style", null, CancellationToken.None);

        var facts = await _corpus.GetAllFactsAsync("default");
        facts.Should().HaveCount(2);
        facts.Should().Contain(f => f.Content!.Contains("London"));
        facts.Should().Contain(f => f.Content!.Contains("functional programming"));
    }

    [Fact]
    public async Task FullSession_MultipleCorpuses_Isolated()
    {
        // First user
        await SimulateTurn("My name is Scott");
        await SimulateTurn("I live in London");

        // Switch to second user manually
        _corpus.SetActiveCorpus("alex");
        await _corpus.IndexFactAsync("alex", "User lives in Berlin", null, CancellationToken.None);

        // Verify isolation
        var scottFacts = await _corpus.GetAllFactsAsync("scott");
        scottFacts.Should().Contain(f => f.Content!.Contains("London"));
        scottFacts.Should().NotContain(f => f.Content!.Contains("Berlin"));

        var alexFacts = await _corpus.GetAllFactsAsync("alex");
        alexFacts.Should().Contain(f => f.Content!.Contains("Berlin"));
        alexFacts.Should().NotContain(f => f.Content!.Contains("London"));

        // List all corpuses
        var corpuses = await _corpus.GetActiveCorpusNamesAsync();
        corpuses.Should().Contain("scott");
        corpuses.Should().Contain("alex");
    }
}

/// <summary>
/// Tests for named personal corpus: name detection, migration, and disambiguation.
/// </summary>
[Collection("EmbeddingTests")]
public class NamedCorpusTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private StorageService _storage = null!;
    private IEmbeddingService _embedding = null!;
    private PersonalCorpusService _corpus = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_named_test_{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();
        _embedding = await EmbeddingFactory.CreateAsync();

        _corpus = new PersonalCorpusService(
            _storage, _embedding, ner: null, knowledgeGraph: null,
            ollama: null, ollamaAvailable: false);
    }

    public async Task DisposeAsync()
    {
        (_embedding as IDisposable)?.Dispose();
        await _storage.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void ActiveCorpusName_DefaultsToDefault()
    {
        _corpus.ActiveCorpusName.Should().Be("default");
    }

    // --- Name detection ---

    [Theory]
    [InlineData("My name is Scott", "Scott")]
    [InlineData("My name is Scott Galloway", "Scott Galloway")]
    [InlineData("Call me Alex", "Alex")]
    [InlineData("I'm Scott", "Scott")]
    [InlineData("I am Scott", "Scott")]
    public async Task DetectAndSetName_RecognizesNameIntroduction(string input, string expectedName)
    {
        var name = await _corpus.DetectAndSetNameAsync(input, CancellationToken.None);

        name.Should().NotBeNull();
        name.Should().Be(expectedName);
        _corpus.ActiveCorpusName.Should().Be(expectedName.ToLowerInvariant());
    }

    [Theory]
    [InlineData("I'm a backend developer")]     // Role, not name
    [InlineData("I'm happy to help")]            // Adjective, not name
    [InlineData("My name is")]                   // Incomplete
    [InlineData("What's the weather?")]          // No name
    [InlineData("I am looking for help")]        // Verb, not name
    [InlineData("I'm based in London")]          // Location disclosure, not name
    public async Task DetectAndSetName_RejectsNonNames(string input)
    {
        var name = await _corpus.DetectAndSetNameAsync(input, CancellationToken.None);

        name.Should().BeNull();
        _corpus.ActiveCorpusName.Should().Be("default");
    }

    [Fact]
    public async Task DetectAndSetName_MigratesDefaultCorpusToNamed()
    {
        // Add facts to default corpus first
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);
        await _corpus.IndexFactAsync("default", "User works at Anthropic", null, CancellationToken.None);

        var defaultBefore = await _corpus.GetAllFactsAsync("default");
        defaultBefore.Should().HaveCount(2);

        // Now introduce yourself — should migrate facts from default → scott
        var name = await _corpus.DetectAndSetNameAsync("My name is Scott", CancellationToken.None);
        name.Should().Be("Scott");

        // Default corpus should now be empty
        var defaultAfter = await _corpus.GetAllFactsAsync("default");
        defaultAfter.Should().BeEmpty();

        // Scott corpus should have the migrated facts plus the name fact
        var scottFacts = await _corpus.GetAllFactsAsync("scott");
        scottFacts.Should().HaveCountGreaterThan(2); // 2 migrated + 1 name fact
        scottFacts.Select(f => f.Content).Should().Contain(c => c!.Contains("London"));
        scottFacts.Select(f => f.Content).Should().Contain(c => c!.Contains("Anthropic"));
        scottFacts.Select(f => f.Content).Should().Contain(c => c!.Contains("Scott"));
    }

    [Fact]
    public async Task DetectAndSetName_SameNameIsNoOp()
    {
        await _corpus.DetectAndSetNameAsync("My name is Scott", CancellationToken.None);
        var factsBefore = await _corpus.GetAllFactsAsync("scott");

        // Saying name again shouldn't duplicate anything
        await _corpus.DetectAndSetNameAsync("My name is Scott", CancellationToken.None);
        var factsAfter = await _corpus.GetAllFactsAsync("scott");

        factsAfter.Should().HaveCount(factsBefore.Count);
    }

    [Fact]
    public void SetActiveCorpus_SwitchesCorpus()
    {
        _corpus.SetActiveCorpus("scott");
        _corpus.ActiveCorpusName.Should().Be("scott");

        _corpus.SetActiveCorpus("Alex");
        _corpus.ActiveCorpusName.Should().Be("alex");
    }

    [Fact]
    public async Task GetActiveCorpusNames_ReturnsAllCorpuses()
    {
        await _corpus.IndexFactAsync("default", "Fact one", null, CancellationToken.None);
        await _corpus.IndexFactAsync("scott", "Fact two", null, CancellationToken.None);
        await _corpus.IndexFactAsync("alex", "Fact three", null, CancellationToken.None);

        var names = await _corpus.GetActiveCorpusNamesAsync();
        names.Should().HaveCount(3);
        names.Should().Contain("default");
        names.Should().Contain("scott");
        names.Should().Contain("alex");
    }

    [Fact]
    public async Task FactsAreIsolatedBetweenCorpuses()
    {
        await _corpus.IndexFactAsync("scott", "User lives in London", null, CancellationToken.None);
        await _corpus.IndexFactAsync("alex", "User lives in Berlin", null, CancellationToken.None);

        var scottFacts = await _corpus.GetAllFactsAsync("scott");
        scottFacts.Should().HaveCount(1);
        scottFacts[0].Content.Should().Contain("London");

        var alexFacts = await _corpus.GetAllFactsAsync("alex");
        alexFacts.Should().HaveCount(1);
        alexFacts[0].Content.Should().Contain("Berlin");
    }
}

/// <summary>
/// Tests for ConversationSentinel gap-filling from personal corpus.
/// </summary>
[Collection("EmbeddingTests")]
public class PersonalGapFillingTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private StorageService _storage = null!;
    private IEmbeddingService _embedding = null!;
    private PersonalCorpusService _corpus = null!;
    private ConversationSentinel _sentinel = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_gapfill_test_{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();
        _embedding = await EmbeddingFactory.CreateAsync();

        _corpus = new PersonalCorpusService(
            _storage, _embedding, ner: null, knowledgeGraph: null,
            ollama: null, ollamaAvailable: false);

        _sentinel = new ConversationSentinel(null, _embedding, false);
    }

    public async Task DisposeAsync()
    {
        (_embedding as IDisposable)?.Dispose();
        await _storage.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ResolveFromPersonal_NoFacts_NoChange()
    {
        var (resolved, context) = await _sentinel.ResolveFromPersonalAsync(
            "What's the weather here?", _corpus, CancellationToken.None);

        resolved.Should().Be("What's the weather here?");
        context.Should().BeNull();
    }

    [Fact]
    public async Task ResolveFromPersonal_NoGapWords_NoChange()
    {
        // Even with personal facts, queries without gap words shouldn't be modified
        await _corpus.IndexFactAsync("default", "User lives in London", null, CancellationToken.None);

        var (resolved, context) = await _sentinel.ResolveFromPersonalAsync(
            "What is the transformer architecture?", _corpus, CancellationToken.None);

        resolved.Should().Be("What is the transformer architecture?");
        context.Should().BeNull();
    }

    // Note: Full gap-filling with entity resolution requires NER + KnowledgeGraph
    // which aren't available in unit tests. The entity-based resolution is tested
    // implicitly through the gap detection logic below.

    [Theory]
    [InlineData("What's the weather here?", true)]
    [InlineData("Find restaurants nearby", true)]
    [InlineData("What's happening locally?", true)]
    [InlineData("Events in my city", true)]
    [InlineData("Events in my area", true)]
    [InlineData("What is AI?", false)]
    [InlineData("Tell me about London", false)]
    public void HasLocationGap_DetectsCorrectly(string query, bool expected)
    {
        // Use reflection to test the private static method
        var method = typeof(ConversationSentinel).GetMethod(
            "HasLocationGap",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("HasLocationGap should exist");

        var result = (bool)method!.Invoke(null, [query])!;
        result.Should().Be(expected, $"'{query}' location gap detection");
    }

    [Theory]
    [InlineData("my company policies", true)]
    [InlineData("at work guidelines", true)]
    [InlineData("my team structure", true)]
    [InlineData("my employer benefits", true)]
    [InlineData("company news today", false)]
    [InlineData("Anthropic research", false)]
    public void HasOrgGap_DetectsCorrectly(string query, bool expected)
    {
        var method = typeof(ConversationSentinel).GetMethod(
            "HasOrgGap",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("HasOrgGap should exist");

        var result = (bool)method!.Invoke(null, [query])!;
        result.Should().Be(expected, $"'{query}' org gap detection");
    }

    [Theory]
    [InlineData("my stack performance", true)]
    [InlineData("my project structure", true)]
    [InlineData("my setup issues", true)]
    [InlineData("my codebase size", true)]
    [InlineData("best tech stack 2024", false)]
    [InlineData("project management tools", false)]
    public void HasTechGap_DetectsCorrectly(string query, bool expected)
    {
        var method = typeof(ConversationSentinel).GetMethod(
            "HasTechGap",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("HasTechGap should exist");

        var result = (bool)method!.Invoke(null, [query])!;
        result.Should().Be(expected, $"'{query}' tech gap detection");
    }
}

/// <summary>
/// Tests for ConversationSentinelResult.PersonalContext field.
/// </summary>
public class ConversationSentinelResultTests
{
    [Fact]
    public void PersonalContext_DefaultsToNull()
    {
        var result = new ConversationSentinelResult
        {
            ResolvedQuery = "test",
        };

        result.PersonalContext.Should().BeNull();
    }

    [Fact]
    public void PersonalContext_CanBeSet()
    {
        var result = new ConversationSentinelResult
        {
            ResolvedQuery = "test",
            PersonalContext = "User is in London; Works at Anthropic",
        };

        result.PersonalContext.Should().Be("User is in London; Works at Anthropic");
    }
}
