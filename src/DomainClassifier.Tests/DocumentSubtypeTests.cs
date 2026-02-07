using DomainClassifier.Narrative;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Tests;

/// <summary>
///     Tests for InferDocumentSubtype logic via NarrativeDomainPlugin.EnrichAsync.
/// </summary>
public class DocumentSubtypeTests
{
    private readonly NarrativeDomainPlugin _plugin = new(NullLogger<NarrativeDomainPlugin>.Instance);

    [Fact]
    public async Task Subtype_PlayDetection_HighDialogueWithStageDirections()
    {
        // Build text with >60% dialogue density + stage directions
        var lines = new List<string>
        {
            "Enter HAMLET",
            "\"To be, or not to be, that is the question.\"",
            "\"Whether 'tis nobler in the mind to suffer.\"",
            "\"The slings and arrows of outrageous fortune.\"",
            "\"Or to take arms against a sea of troubles.\"",
            "\"And by opposing end them? To die, to sleep.\"",
            "\"No more; and by a sleep to say we end.\"",
            "\"The heart-ache and the thousand natural shocks.\"",
            "Exit HAMLET",
            "Enter OPHELIA",
            "\"My lord, I have remembrances of yours.\"",
            "\"That I have longed long to re-deliver.\"",
            "\"I pray you, now receive them.\"",
            "\"Rich gifts wax poor when givers prove unkind.\"",
            "Exeunt"
        };

        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = string.Join("\n", lines),
                ContentType = ContentType.DocumentText,
                SourcePath = "/play.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);
        var subtype = result.Signals.FirstOrDefault(s => s.Key == "narrative.document_subtype");

        Assert.NotNull(subtype);
        Assert.Equal("play", subtype.Value?.ToString());
    }

    [Fact]
    public async Task Subtype_PoetryDetection_ShortIrregularLines()
    {
        // Lines all < 60 chars, with high variance (some very short, some ~50 chars)
        // Need >70% lines < 60 chars AND variance > 100
        var lines = new List<string>
        {
            "O!",
            "Shall I compare thee to a summer's day?",
            "No.",
            "Thou art more lovely and more temperate.",
            "Ah!",
            "Rough winds do shake the darling buds.",
            "And summer's lease hath all too short a date.",
            "But",
            "Sometime too hot the eye of heaven shines.",
            "And often is his gold complexion dimmed.",
            "So!",
            "And every fair from fair sometime declines."
        };

        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = string.Join("\n", lines),
                ContentType = ContentType.DocumentText,
                SourcePath = "/poem.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);
        var subtype = result.Signals.FirstOrDefault(s => s.Key == "narrative.document_subtype");

        Assert.NotNull(subtype);
        Assert.Equal("poetry", subtype.Value?.ToString());
    }

    [Fact]
    public async Task Subtype_LetterDetection_DearPatterns()
    {
        var text = """
                   Dear Margaret,

                   I write to you from the frozen north. The ice is all around us.

                   Dear Sister,

                   I hope this second letter finds you well. The expedition continues.

                   Dear Mother,

                   Please forgive the delay in writing. There is much to tell about the voyage.
                   We have encountered strange phenomena on the ice.
                   The crew grows restless but I remain steadfast in my purpose.
                   """;

        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = text,
                ContentType = ContentType.DocumentText,
                SourcePath = "/letter.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);
        var subtype = result.Signals.FirstOrDefault(s => s.Key == "narrative.document_subtype");

        Assert.NotNull(subtype);
        Assert.Equal("letter", subtype.Value?.ToString());
    }

    [Fact]
    public async Task Subtype_ShortStory_SmallNarrativeText()
    {
        // < 20,000 chars, no play/poetry/letter indicators
        var text = """
                   The old man sat by the fire, thinking of days gone by.
                   He remembered his youth and the adventures he had shared with friends.
                   The wind howled outside, but inside it was warm and comforting.
                   He picked up the book and began to read, losing himself in the story.
                   Outside, the snow continued to fall, blanketing the world in white.
                   """;

        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = text,
                ContentType = ContentType.DocumentText,
                SourcePath = "/story.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);
        var subtype = result.Signals.FirstOrDefault(s => s.Key == "narrative.document_subtype");

        Assert.NotNull(subtype);
        Assert.Equal("short_story", subtype.Value?.ToString());
    }

    [Fact]
    public async Task Subtype_Novel_LongNarrativeText()
    {
        // > 20,000 chars with no play/poetry/letter indicators
        var paragraph = "The sun set over the rolling hills, casting long shadows across the valley floor. " +
                        "She walked along the path, her thoughts wandering to distant memories. " +
                        "The birds sang their evening songs as the world settled into dusk. " +
                        "He followed behind, keeping a respectful distance, lost in his own thoughts. ";

        // Repeat to exceed 20k chars
        var text = string.Concat(Enumerable.Repeat(paragraph + "\n", 80));

        var chunks = new List<ContentChunk>
        {
            new()
            {
                Id = "chunk_1",
                Text = text,
                ContentType = ContentType.DocumentText,
                SourcePath = "/novel.txt"
            }
        };

        var result = await _plugin.EnrichAsync(chunks);
        var subtype = result.Signals.FirstOrDefault(s => s.Key == "narrative.document_subtype");

        Assert.NotNull(subtype);
        Assert.Equal("novel", subtype.Value?.ToString());
    }
}
