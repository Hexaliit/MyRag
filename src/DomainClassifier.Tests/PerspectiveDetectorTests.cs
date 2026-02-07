using DomainClassifier.Narrative.Extractors;

namespace DomainClassifier.Tests;

public class PerspectiveDetectorTests
{
    [Fact]
    public void Detect_FirstPersonText_ReturnsFirstPerson()
    {
        var text = """
                   I went to the store to buy some bread. My friend was there, and she waved at me.
                   I walked over and we talked for a while. I told her about my new job.
                   She congratulated me and I felt happy about it. I decided to celebrate.
                   """;

        var (perspective, confidence) = PerspectiveDetector.Detect(text);

        Assert.Equal("first-person", perspective);
        Assert.True(confidence >= 0.6,
            $"Expected confidence >= 0.6 for first-person text, got {confidence}");
    }

    [Fact]
    public void Detect_ThirdPersonText_ReturnsThirdPerson()
    {
        var text = """
                   She walked down the street, her coat pulled tight against the cold.
                   She turned the corner and saw him standing by the lamppost.
                   He waved at her and she quickened her pace. She reached him and they embraced.
                   """;

        var (perspective, confidence) = PerspectiveDetector.Detect(text);

        Assert.True(perspective == "third-person" || perspective == "omniscient",
            $"Expected third-person or omniscient, got {perspective}");
        Assert.True(confidence >= 0.5,
            $"Expected confidence >= 0.5 for third-person text, got {confidence}");
    }

    [Fact]
    public void Detect_EpistolaryText_ReturnsEpistolary()
    {
        var text = """
                   Dear Margaret,

                   I write to you from the northern seas. The weather has been most treacherous.

                   Dear Sister,

                   I hope this letter finds you well. I have news of great importance.
                   """;

        var (perspective, confidence) = PerspectiveDetector.Detect(text);

        Assert.Equal("epistolary", perspective);
        Assert.True(confidence >= 0.6,
            $"Expected confidence >= 0.6 for epistolary text, got {confidence}");
    }

    [Fact]
    public void Detect_EmptyText_ReturnsUnknown()
    {
        var (perspective, confidence) = PerspectiveDetector.Detect("");

        Assert.Equal("unknown", perspective);
        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public void Detect_OmniscientText_ReturnsOmniscientOrThirdPerson()
    {
        var text = """
                   He thought about his future with great uncertainty.
                   She felt the same way, though she would never admit it to him.
                   He wondered if she knew what he was thinking.
                   She knew, of course. She had always known his thoughts better than her own.
                   He felt a surge of courage, and she sensed his determination.
                   """;

        var (perspective, confidence) = PerspectiveDetector.Detect(text);

        Assert.True(perspective == "omniscient" || perspective == "third-person",
            $"Expected omniscient or third-person, got {perspective}");
    }

    [Fact]
    public void Detect_MixedOrUnclear_ReturnsLowConfidence()
    {
        // Text with some pronouns but not enough density to hit main thresholds
        // Uses lots of neutral padding to dilute pronoun density below the 0.03/0.02 thresholds
        var text = "The weather was pleasant that afternoon. " +
                   "The clock struck four. " +
                   "A carriage arrived at the gate. " +
                   "The garden bloomed with roses and lilacs. " +
                   "The windows of the house were open. " +
                   "The road stretched far into the distance. " +
                   "She walked along the path. " + // one third-person
                   "I noticed the clouds gathering. " + // one first-person
                   "The trees swayed gently in the breeze. " +
                   "The old stone wall bordered the property.";

        var (perspective, confidence) = PerspectiveDetector.Detect(text);

        // With very diluted pronoun density, should be low confidence or unknown
        Assert.True(confidence <= 0.6,
            $"Expected confidence <= 0.6 for mixed/diluted text, got {confidence}");
    }

    [Fact]
    public void Detect_NeutralText_ReturnsUnknown()
    {
        // Technical text with no pronouns
        var text = "The algorithm processes data in batches. Each batch contains 100 records. " +
                   "Processing completes within 5 seconds on average.";

        var (perspective, confidence) = PerspectiveDetector.Detect(text);

        Assert.Equal("unknown", perspective);
        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public void Detect_WhitespaceOnly_ReturnsUnknown()
    {
        var (perspective, confidence) = PerspectiveDetector.Detect("   \t\n  ");

        Assert.Equal("unknown", perspective);
        Assert.Equal(0.0, confidence);
    }
}
