using DomainClassifier.Narrative;

namespace DomainClassifier.Tests;

public class NarrativeKeywordClassifierTests
{
    [Fact]
    public void Classify_FrankensteinText_HighConfidence()
    {
        var text = """
                   Chapter 1

                   I am by birth a Genevese, and my family is one of the most distinguished of that republic.
                   My ancestors had been for many years counsellors and syndics, and my father had filled several
                   public situations with honour and reputation. He was respected by all who knew him for his
                   integrity and indefatigable attention to public business. He passed his younger days perpetually
                   occupied by the affairs of his country; a variety of circumstances had prevented his marrying
                   early, nor was it until the decline of life that he became a husband and the father of a family.

                   "Do not let me hear you speak so," said Elizabeth. "It was not so in old days."

                   The narrator continued his tale of horror and dread, describing the creature he had created
                   in his laboratory. The protagonist shuddered at the memory of that fateful night.
                   """;

        var result = NarrativeKeywordClassifier.Classify(text);

        Assert.Equal("narrative", result.DomainId);
        Assert.True(result.Confidence > 0.6,
            $"Expected confidence > 0.6 for Frankenstein text, got {result.Confidence}");
    }

    [Fact]
    public void Classify_FinancialText_LowConfidence()
    {
        var text = "The company reported quarterly earnings of $2.5B with revenue growth of 15%. " +
                   "EBITDA margins improved and the balance sheet remains strong. " +
                   "The board approved a dividend increase.";

        var result = NarrativeKeywordClassifier.Classify(text);

        Assert.Equal("narrative", result.DomainId);
        Assert.True(result.Confidence < 0.4,
            $"Expected confidence < 0.4 for financial text, got {result.Confidence}");
    }

    [Fact]
    public void Classify_EmptyText_ZeroConfidence()
    {
        var result = NarrativeKeywordClassifier.Classify("");

        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Classify_PrideAndPrejudiceExcerpt_HighConfidence()
    {
        var text = """
                   Chapter 1

                   It is a truth universally acknowledged, that a single man in possession of a good fortune,
                   must be in want of a wife.

                   "My dear Mr. Bennet," said his lady to him one day, "have you heard that Netherfield Park
                   is let at last?"

                   Mr. Bennet replied that he had not.

                   "But it is," returned she; "for Mrs. Long has just been here, and she told me all about it."

                   Mr. Bennet made no answer.

                   "Do you not want to know who has taken it?" cried his wife impatiently.
                   """;

        var result = NarrativeKeywordClassifier.Classify(text);

        Assert.Equal("narrative", result.DomainId);
        Assert.True(result.Confidence > 0.6,
            $"Expected confidence > 0.6 for Pride and Prejudice, got {result.Confidence}");
    }

    [Fact]
    public void Classify_DialogueHeavyText_HighConfidence()
    {
        var text = """
                   "I cannot believe you said that," exclaimed Mary.
                   "Well, it's the truth," replied John.
                   "You have no idea what you're talking about," whispered Sarah.
                   "Perhaps not," he murmured, "but I stand by it."
                   "Very well," said Mary. "Then there is nothing more to discuss."
                   "Nothing at all," agreed John with a sigh.
                   """;

        var result = NarrativeKeywordClassifier.Classify(text);

        Assert.True(result.Confidence > 0.4,
            $"Expected confidence > 0.4 for dialogue-heavy text, got {result.Confidence}");
    }

    [Fact]
    public void ComputeDialogueDensity_HighDialogue_ReturnsHighRatio()
    {
        var text = """
                   "Hello," said John.
                   "Hi there," replied Mary.
                   "How are you?"
                   "I'm fine, thanks."
                   He nodded silently.
                   """;

        var density = NarrativeKeywordClassifier.ComputeDialogueDensity(text);

        Assert.True(density > 0.5, $"Expected density > 0.5, got {density}");
    }

    [Fact]
    public void ComputeDialogueDensity_NoDialogue_ReturnsZeroOrLow()
    {
        var text = """
                   The sun set over the hills.
                   Birds flew across the sky.
                   The river flowed gently through the valley.
                   """;

        var density = NarrativeKeywordClassifier.ComputeDialogueDensity(text);

        Assert.True(density < 0.1, $"Expected density < 0.1, got {density}");
    }

    [Fact]
    public void Classify_ChapterHeadings_BoostConfidence()
    {
        var text = """
                   Chapter 1

                   The story begins here.

                   Chapter 2

                   The adventure continues in chapter two.

                   Chapter 3

                   The climax approaches as the protagonist faces the antagonist.
                   """;

        var result = NarrativeKeywordClassifier.Classify(text);

        Assert.True(result.Confidence > 0.4,
            $"Expected confidence > 0.4 for text with chapter headings, got {result.Confidence}");
    }

    [Fact]
    public void Classify_TechnicalText_VeryLowConfidence()
    {
        var text = "The API endpoint returns JSON data. " +
                   "Configure the database connection string in appsettings.json. " +
                   "Run the unit tests with dotnet test.";

        var result = NarrativeKeywordClassifier.Classify(text);

        Assert.True(result.Confidence <= 0.4,
            $"Expected confidence <= 0.4 for technical text, got {result.Confidence}");
    }

    [Fact]
    public void Classify_WhitespaceOnly_ZeroConfidence()
    {
        var result = NarrativeKeywordClassifier.Classify("   \t\n  ");

        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Classify_AlwaysReturnsNarrativeDomainId()
    {
        // Even for non-narrative text, DomainId should always be "narrative"
        var result = NarrativeKeywordClassifier.Classify("Some random text about nothing.");

        Assert.Equal("narrative", result.DomainId);
        Assert.Equal("Narrative Domain", result.DomainName);
    }
}
