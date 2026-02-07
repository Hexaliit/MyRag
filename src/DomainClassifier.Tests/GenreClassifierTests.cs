using DomainClassifier.Narrative.Extractors;

namespace DomainClassifier.Tests;

public class GenreClassifierTests
{
    [Fact]
    public void Classify_HorrorText_ReturnsHorrorOrSciFi()
    {
        var text = """
                   The creature opened its hideous eyes and gazed upon me with horror and dread.
                   I had created a monster in my laboratory, an experiment gone terribly wrong.
                   The wretched creature filled me with fear and terror as it rose from the table.
                   I felt agony and misery at the sight of this fiend I had brought to life.
                   """;

        var (genre, confidence) = GenreClassifier.Classify(text);

        Assert.True(genre == "horror" || genre == "sci-fi",
            $"Expected horror or sci-fi for Frankenstein-like text, got {genre}");
        Assert.True(confidence > 0.3,
            $"Expected confidence > 0.3, got {confidence}");
    }

    [Fact]
    public void Classify_RomanceText_ReturnsRomance()
    {
        var text = """
                   Her heart fluttered with love and affection for the handsome gentleman.
                   The courtship had been long, but their devotion to each other was undeniable.
                   He proposed at the ball, and she accepted with tender embrace.
                   Their marriage would be the talk of the season, and all admired the beautiful bride.
                   """;

        var (genre, confidence) = GenreClassifier.Classify(text);

        Assert.Equal("romance", genre);
        Assert.True(confidence > 0.3,
            $"Expected confidence > 0.3 for romance text, got {confidence}");
    }

    [Fact]
    public void Classify_MysteryText_ReturnsMystery()
    {
        var text = """
                   The detective examined the clue left at the crime scene.
                   The suspect had an alibi, but the evidence told a different story.
                   Inspector Holmes began his investigation, interviewing each witness carefully.
                   The murder weapon was found, but the motive remained unclear.
                   """;

        var (genre, confidence) = GenreClassifier.Classify(text);

        Assert.Equal("mystery", genre);
        Assert.True(confidence > 0.3,
            $"Expected confidence > 0.3 for mystery text, got {confidence}");
    }

    [Fact]
    public void Classify_FantasyText_ReturnsFantasy()
    {
        var text = """
                   The wizard cast a powerful spell from the top of the ancient tower.
                   A dragon soared over the kingdom, its enchantment protecting the realm.
                   The quest for the lost sword led them through dark forests and forgotten ruins.
                   """;

        var (genre, confidence) = GenreClassifier.Classify(text);

        Assert.Equal("fantasy", genre);
        Assert.True(confidence > 0.3,
            $"Expected confidence > 0.3 for fantasy text, got {confidence}");
    }

    [Fact]
    public void Classify_EmptyText_ReturnsUnknown()
    {
        var (genre, confidence) = GenreClassifier.Classify("");

        Assert.Equal("unknown", genre);
        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public void ClassifyAll_MultiGenreText_ReturnsMultiple()
    {
        var text = """
                   The detective investigated the murder in the haunted castle.
                   Magic filled the air as the wizard examined the crime scene.
                   The suspect, a creature of horror, had fled the kingdom.
                   """;

        var results = GenreClassifier.ClassifyAll(text);

        Assert.True(results.Count >= 2,
            $"Expected at least 2 genre matches for multi-genre text, got {results.Count}");
    }

    [Fact]
    public void ClassifyAll_OrderedByConfidenceDescending()
    {
        var text = """
                   The detective found a clue at the crime scene. The suspect fled.
                   Evidence pointed to the murder suspect. The investigation continued.
                   The witness described the alibi in detail. The inspector noted it all.
                   Meanwhile, a ghost haunted the old house nearby.
                   """;

        var results = GenreClassifier.ClassifyAll(text);

        Assert.True(results.Count >= 2);
        // Should be ordered by confidence (highest first)
        for (var i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].Confidence >= results[i].Confidence,
                $"Expected descending order: {results[i - 1].Genre}={results[i - 1].Confidence} " +
                $"should be >= {results[i].Genre}={results[i].Confidence}");
        }
        // Mystery should be the top genre (most keyword hits)
        Assert.Equal("mystery", results[0].Genre);
    }

    [Fact]
    public void ClassifyAll_EmptyText_ReturnsEmpty()
    {
        var results = GenreClassifier.ClassifyAll("");

        Assert.Empty(results);
    }

    [Fact]
    public void Classify_HistoricalText_ReturnsHistorical()
    {
        var text = """
                   The king led his army into battle during the medieval conquest.
                   The empire expanded as the dynasty consolidated its power.
                   The queen watched from the throne as revolution swept the land.
                   """;

        var (genre, confidence) = GenreClassifier.Classify(text);

        Assert.Equal("historical", genre);
        Assert.True(confidence > 0.3,
            $"Expected confidence > 0.3 for historical text, got {confidence}");
    }

    [Fact]
    public void Classify_LiteraryText_ReturnsLiterary()
    {
        var text = """
                   The gentleman observed the lady with keen irony. Society's manners
                   demanded propriety, yet his pride and her prejudice created a tension
                   that wit alone could not resolve. His fortune and estate meant nothing
                   against her sharp observation of his disposition and temperament.
                   """;

        var (genre, confidence) = GenreClassifier.Classify(text);

        Assert.Equal("literary", genre);
        Assert.True(confidence > 0.3,
            $"Expected confidence > 0.3 for literary text, got {confidence}");
    }

    [Fact]
    public void Classify_NoMatchingKeywords_ReturnsUnknown()
    {
        var text = "The quick brown fox jumps over the lazy dog repeatedly.";

        var (genre, confidence) = GenreClassifier.Classify(text);

        Assert.Equal("unknown", genre);
        Assert.Equal(0.0, confidence);
    }
}
