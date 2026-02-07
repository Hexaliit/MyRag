using DomainClassifier.Narrative.Extractors;

namespace DomainClassifier.Tests;

public class CharacterExtractorTests
{
    [Fact]
    public void Extract_AfterVerbAttribution_ExtractsCharacter()
    {
        var text = "\"I have not,\" said Elizabeth.";

        var entities = CharacterExtractor.Extract(text);

        Assert.Contains(entities, e => e.Text.Contains("Elizabeth") && e.EntityType == "character");
    }

    [Fact]
    public void Extract_BeforeVerbAttribution_ExtractsCharacter()
    {
        var text = "Elizabeth replied with a smile. John asked about the weather.";

        var entities = CharacterExtractor.Extract(text);

        Assert.Contains(entities, e => e.Text.Contains("Elizabeth") && e.EntityType == "character");
        Assert.Contains(entities, e => e.Text.Contains("John") && e.EntityType == "character");
    }

    [Fact]
    public void Extract_TitleAndName_ExtractsWithTitle()
    {
        var text = "Mr. Darcy arrived at the ball. Lady Catherine was not pleased.";

        var entities = CharacterExtractor.Extract(text);

        Assert.Contains(entities, e => e.Text == "Mr. Darcy" && e.EntityType == "character");
        Assert.Contains(entities, e => e.Text == "Lady Catherine" && e.EntityType == "character");
    }

    [Fact]
    public void Extract_IntroductionPattern_ExtractsCharacter()
    {
        var text = "He was a man named Victor, known as the creator of the creature.";

        var entities = CharacterExtractor.Extract(text);

        Assert.Contains(entities, e => e.Text.Contains("Victor") && e.EntityType == "character");
    }

    [Fact]
    public void Extract_FrequencyAffectsConfidence_HighMentionsHighConfidence()
    {
        var lines = Enumerable.Range(0, 12)
            .Select(i => $"Elizabeth said something in line {i}.");
        var text = string.Join("\n", lines);

        var entities = CharacterExtractor.Extract(text);
        var elizabeth = entities.FirstOrDefault(e => e.Text.Contains("Elizabeth"));

        Assert.NotNull(elizabeth);
        Assert.True(elizabeth.Confidence >= 0.95,
            $"Expected confidence >= 0.95 for 12 mentions, got {elizabeth.Confidence}");
    }

    [Fact]
    public void Extract_FewMentions_LowerConfidence()
    {
        var text = "\"Hello,\" said Margaret. That was all she said that day.";

        var entities = CharacterExtractor.Extract(text);
        var margaret = entities.FirstOrDefault(e => e.Text.Contains("Margaret"));

        Assert.NotNull(margaret);
        Assert.True(margaret.Confidence <= 0.85,
            $"Expected confidence <= 0.85 for few mentions, got {margaret.Confidence}");
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        var entities = CharacterExtractor.Extract("");

        Assert.Empty(entities);
    }

    [Fact]
    public void Extract_OrderedByMentionCount()
    {
        var text = """
                   Elizabeth said hello. Elizabeth replied warmly. Elizabeth asked about dinner.
                   Elizabeth exclaimed with joy. Elizabeth whispered a secret.
                   John said one thing only.
                   """;

        var entities = CharacterExtractor.Extract(text);

        if (entities.Count >= 2)
        {
            // Elizabeth should come first (more mentions)
            Assert.Contains("Elizabeth", entities[0].Text);
        }
    }

    [Fact]
    public void Extract_FalsePositives_FiltersCommonWords()
    {
        // These words look like proper nouns in certain contexts but should be filtered
        var text = "\"The weather is fine,\" said The. " +
                   "\"Indeed it is,\" replied Chapter. " +
                   "\"Monday will be better,\" declared London.";

        var entities = CharacterExtractor.Extract(text);

        Assert.DoesNotContain(entities, e => e.Text == "The");
        Assert.DoesNotContain(entities, e => e.Text == "Chapter");
        Assert.DoesNotContain(entities, e => e.Text == "Monday");
        Assert.DoesNotContain(entities, e => e.Text == "London");
    }

    [Fact]
    public void Extract_FalsePositives_FiltersMonthsAndDays()
    {
        var text = "\"Let us go,\" said March. \"Today is good,\" replied Sunday.";

        var entities = CharacterExtractor.Extract(text);

        Assert.DoesNotContain(entities, e => e.Text == "March");
        Assert.DoesNotContain(entities, e => e.Text == "Sunday");
    }

    [Fact]
    public void Extract_MultiplePatterns_MergesMentions()
    {
        // Same name via multiple patterns should merge into a single entity with cumulative mentions
        var text = """
                   Mr. Darcy entered the room. Darcy said hello. "Good evening," replied Darcy.
                   Mr. Darcy bowed politely. Darcy asked about the music. Mr. Darcy smiled.
                   """;

        var entities = CharacterExtractor.Extract(text);
        var darcy = entities.FirstOrDefault(e => e.Text.Contains("Darcy"));

        Assert.NotNull(darcy);
        // Multiple mentions from different patterns should accumulate
        Assert.True(darcy.Metadata?["mentions"] is int count && count >= 3,
            $"Expected at least 3 mentions for Darcy");
    }

    [Fact]
    public void Extract_ShortName_Filtered()
    {
        // Single character or very short names should be filtered
        var text = "\"Go,\" said A.";

        var entities = CharacterExtractor.Extract(text);

        Assert.DoesNotContain(entities, e => e.Text == "A");
    }
}
