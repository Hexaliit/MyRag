using DomainClassifier.Narrative;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Tests;

public class NarrativeDomainPluginTests
{
    private readonly NarrativeDomainPlugin _plugin = new(NullLogger<NarrativeDomainPlugin>.Instance);

    [Fact]
    public async Task EnrichAsync_NarrativeText_ReturnsSignals()
    {
        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = """
                       Chapter 1

                       "My dear Mr. Bennet," said his lady, "have you heard that Netherfield Park is let at last?"
                       Mr. Bennet replied that he had not.
                       "But it is," returned she; "for Mrs. Long has just been here, and she told me all about it."
                       """,
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);

        Assert.NotNull(result);
        Assert.True(result.Entities.Count > 0, "Expected at least one entity");
        Assert.True(result.Signals.Count > 0, "Expected at least one signal");

        // Check for expected signal keys
        var signalKeys = result.Signals.Select(s => s.Key).ToHashSet();
        Assert.Contains("narrative.character_count", signalKeys);
        Assert.Contains("narrative.dialogue_density", signalKeys);
        Assert.Contains("narrative.document_subtype", signalKeys);
    }

    [Fact]
    public async Task EnrichAsync_LongNarrative_ExtractsCharactersAndPerspective()
    {
        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = """
                       I went to the store. My friend Elizabeth was there. I told her about my plans.
                       "That sounds wonderful," said Elizabeth. I was glad she agreed.
                       "I have always thought so," replied Elizabeth. I felt encouraged by her words.
                       Elizabeth said she would help me. I thanked her profusely.
                       "You are most welcome," whispered Elizabeth. I smiled with gratitude.
                       Elizabeth exclaimed that it would be a grand adventure.
                       """,
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);

        // Should detect characters
        var characters = result.Entities.Where(e => e.EntityType == "character").ToList();
        Assert.True(characters.Count > 0, "Expected at least one character");
        Assert.Contains(characters, c => c.Text.Contains("Elizabeth"));

        // Should detect perspective
        var perspectiveSignal = result.Signals.FirstOrDefault(s => s.Key == "narrative.perspective");
        Assert.NotNull(perspectiveSignal);
        Assert.Equal("first-person", perspectiveSignal.Value);
    }

    [Fact]
    public async Task EnrichAsync_EmptyChunks_ReturnsMinimalResult()
    {
        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = "",
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);

        Assert.NotNull(result);
        // Should still have some signals (character_count = 0, dialogue_density, etc.)
        Assert.True(result.Signals.Count > 0);
    }

    [Fact]
    public async Task EnrichAsync_DetectsGenre()
    {
        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = """
                       The detective examined the clue left at the crime scene.
                       The suspect had an alibi, but the evidence told a different story.
                       Inspector Holmes began his investigation, interviewing each witness.
                       The murder weapon was discovered hidden beneath the floorboards.
                       """,
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);

        var genreSignal = result.Signals.FirstOrDefault(s => s.Key == "narrative.genre");
        Assert.NotNull(genreSignal);
        Assert.Equal("mystery", genreSignal.Value);
    }

    [Fact]
    public async Task EnrichAsync_PrimaryCharactersOrderedByMentions()
    {
        var lines = new List<string>();
        for (var i = 0; i < 12; i++)
            lines.Add($"Elizabeth said something in line {i}.");
        lines.Add("John said one thing only.");

        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = string.Join("\n", lines),
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);

        var primaryChars = result.Signals.FirstOrDefault(s => s.Key == "narrative.primary_characters");
        Assert.NotNull(primaryChars);
        var charList = primaryChars.Value as List<string>;
        Assert.NotNull(charList);
        Assert.True(charList.Count >= 1);
        // Elizabeth should come first (more mentions)
        Assert.Equal("Elizabeth", charList[0]);
    }

    [Fact]
    public async Task EnrichAsync_ExtractsSettings()
    {
        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = "They arrived at Pemberley and later traveled to London during the 19th century.",
                ContentType = ContentType.DocumentText,
                SourcePath = "/test.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);

        var settingsSignal = result.Signals.FirstOrDefault(s => s.Key == "narrative.settings");
        Assert.NotNull(settingsSignal);
        var settings = settingsSignal.Value as List<string>;
        Assert.NotNull(settings);
        Assert.True(settings.Count > 0);

        var timePeriod = result.Signals.FirstOrDefault(s => s.Key == "narrative.time_period");
        Assert.NotNull(timePeriod);
    }

    [Fact]
    public async Task ClassifyAsync_NarrativeText_HighConfidence()
    {
        var result = await _plugin.ClassifyAsync(
            "Chapter 1. The protagonist ventured forth on a narrative journey of great fiction.");

        Assert.Equal("narrative", result.DomainId);
        Assert.True(result.Confidence > 0.4);
    }

    [Fact]
    public async Task ClassifyAsync_NonNarrativeText_LowConfidence()
    {
        var result = await _plugin.ClassifyAsync(
            "The quarterly earnings report showed strong revenue growth in the semiconductor sector.");

        Assert.Equal("narrative", result.DomainId);
        Assert.True(result.Confidence < 0.4);
    }

    [Fact]
    public void DomainId_IsNarrative()
    {
        Assert.Equal("narrative", _plugin.DomainId);
        Assert.Equal("Narrative Domain", _plugin.Name);
    }

    [Fact]
    public void EntityTypes_ContainsExpectedTypes()
    {
        Assert.Contains("character", _plugin.EntityTypes);
        Assert.Contains("dialogue", _plugin.EntityTypes);
        Assert.Contains("setting_location", _plugin.EntityTypes);
        Assert.Contains("time_period", _plugin.EntityTypes);
        Assert.Contains("atmosphere", _plugin.EntityTypes);
    }
}
