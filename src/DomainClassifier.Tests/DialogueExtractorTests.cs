using DomainClassifier.Narrative.Extractors;

namespace DomainClassifier.Tests;

public class DialogueExtractorTests
{
    [Fact]
    public void Extract_DoubleQuotedDialogue_Extracts()
    {
        var text = "She looked at him and said \"I will never forget this moment\" before leaving.";

        var entities = DialogueExtractor.Extract(text);

        Assert.Contains(entities, e =>
            e.EntityType == "dialogue" &&
            e.Text.Contains("never forget"));
    }

    [Fact]
    public void Extract_AttributedDialogue_IncludesSpeaker()
    {
        var text = "\"I cannot believe it,\" said Elizabeth.";

        var entities = DialogueExtractor.Extract(text);

        var attributed = entities.FirstOrDefault(e =>
            e.Metadata?.ContainsKey("speaker") == true &&
            (string?)e.Metadata["speaker"] == "Elizabeth");

        Assert.NotNull(attributed);
        Assert.True(attributed.Confidence >= 0.9);
    }

    [Fact]
    public void Extract_MultipleDialogues_ExtractsAll()
    {
        var text = """
                   "Good morning," said John.
                   "Good morning to you as well," replied Mary.
                   "Shall we go?" he asked.
                   """;

        var entities = DialogueExtractor.Extract(text);

        Assert.True(entities.Count >= 2,
            $"Expected at least 2 dialogue extractions, got {entities.Count}");
    }

    [Fact]
    public void ComputeDialogueDensity_AllDialogue_ReturnsHigh()
    {
        var text = """
                   "First line of dialogue."
                   "Second line of dialogue."
                   "Third line of dialogue."
                   "Fourth line of dialogue."
                   """;

        var density = DialogueExtractor.ComputeDialogueDensity(text);

        Assert.True(density > 0.8, $"Expected density > 0.8 for all dialogue, got {density}");
    }

    [Fact]
    public void ComputeDialogueDensity_NoDialogue_ReturnsLow()
    {
        var text = """
                   The wind blew through the trees.
                   Birds sang their morning songs.
                   A deer grazed in the meadow.
                   """;

        var density = DialogueExtractor.ComputeDialogueDensity(text);

        Assert.True(density < 0.1, $"Expected density < 0.1 for no dialogue, got {density}");
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        var entities = DialogueExtractor.Extract("");

        Assert.Empty(entities);
    }

    [Fact]
    public void Extract_ShortQuotes_Skipped()
    {
        // Very short double-quoted text (< 3 chars) should be skipped
        var text = "She said \"No\" and walked away.";

        var entities = DialogueExtractor.Extract(text);

        // "No" is only 2 chars, should be filtered out by the < 3 check
        Assert.DoesNotContain(entities, e =>
            e.EntityType == "dialogue" &&
            e.Text == "No" &&
            e.Metadata?.ContainsKey("attributed") == true &&
            (bool)e.Metadata["attributed"]! == false);
    }

    [Fact]
    public void Extract_AttributedDialogue_DoesNotDuplicateAsUnattributed()
    {
        var text = "\"I cannot believe it,\" said Elizabeth.";

        var entities = DialogueExtractor.Extract(text);

        // Should have exactly one dialogue entity (the attributed one), not a duplicate unattributed
        var dialogueEntities = entities.Where(e => e.EntityType == "dialogue").ToList();
        Assert.Single(dialogueEntities);
        Assert.True((bool)dialogueEntities[0].Metadata!["attributed"]!);
    }

    [Fact]
    public void ComputeDialogueDensity_MixedContent_ReturnsMiddle()
    {
        var text = """
                   "Hello there," said John.
                   The sun was setting over the hills.
                   "Goodbye," she replied.
                   The birds flew away silently.
                   """;

        var density = DialogueExtractor.ComputeDialogueDensity(text);

        Assert.True(density > 0.3 && density < 0.7,
            $"Expected density between 0.3 and 0.7 for mixed content, got {density}");
    }

    [Fact]
    public void ComputeDialogueDensity_EmptyText_ReturnsZero()
    {
        var density = DialogueExtractor.ComputeDialogueDensity("");

        Assert.Equal(0.0, density);
    }
}
