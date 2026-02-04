using DoomSummarizer.Commands;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests;

public class AdaptiveChunkingTests
{
    // ── ComputeComplexityProfile ─────────────────────────────────────

    [Fact]
    public void ComputeComplexityProfile_Fiction_DialogueHeavy()
    {
        var text = """
            Chapter One

            "Good morning, Watson," said Holmes, leaning back in his chair.
            "I trust you slept well?" He reached for his pipe.
            "Tolerably," Watson replied. "But tell me, what case is this?"
            "A most singular affair," Holmes whispered.
            "The woman arrived at Baker Street yesterday."
            "What did she want?" asked Watson eagerly.
            "She wished to consult me about her missing husband," said Holmes.
            "How extraordinary!" Watson exclaimed. "Do go on."
            Mr. Bartholomew Sholto had vanished from his locked study.
            Inspector Lestrade was baffled by the complete lack of evidence.
            """;

        var profile = ScrollCommand.ComputeComplexityProfile(text, IngestDocumentType.Fiction);

        profile.DialogueDensity.Should().BeGreaterThan(0.2f, "text has heavy dialogue");
        profile.CompositeComplexity.Should().BeInRange(0.05f, 0.95f);
        profile.TotalChars.Should().Be(text.Length);
    }

    [Fact]
    public void ComputeComplexityProfile_Technical_CodeHeavy()
    {
        var text = """
            # Installation Guide

            ## Getting Started

            Install the package:

            ```bash
            npm install lucidrag
            ```

            Configure the API:

            ```javascript
            const config = {
                apiKey: process.env.API_KEY,
                model: 'gpt-4',
                temperature: 0.7,
                maxTokens: 2048,
                streaming: true,
                retryCount: 3
            };
            const client = new LucidRAG(config);
            await client.initialize();
            ```

            ## Docker Deployment

            ```yaml
            services:
              app:
                image: lucidrag:latest
                ports:
                  - "8080:8080"
            ```
            """;

        var profile = ScrollCommand.ComputeComplexityProfile(text, IngestDocumentType.Technical);

        profile.CodeDensity.Should().BeGreaterThan(0.2f, "text has significant code blocks");
        profile.StructuralDensity.Should().BeGreaterThan(0f, "text has headings");
        profile.CompositeComplexity.Should().BeInRange(0.05f, 0.95f);
    }

    [Fact]
    public void ComputeComplexityProfile_EmptyText_ReturnsDefault()
    {
        var profile = ScrollCommand.ComputeComplexityProfile("", IngestDocumentType.Unknown);

        profile.TotalChars.Should().Be(0);
        profile.CompositeComplexity.Should().Be(0f);
    }

    // ── ComputeChunkTargets ─────────────────────────────────────────

    [Fact]
    public void ComputeChunkTargets_Novella_ProducesSmallChunks()
    {
        // 122K chars ≈ novella length (Sign of the Four)
        var profile = new DocumentComplexityProfile
        {
            CompositeComplexity = 0.5f,
            TotalChars = 122_000
        };

        var (target, min) = ScrollCommand.ComputeChunkTargets(122_000, IngestDocumentType.Fiction, profile);

        // Base for 50K-150K Fiction = 600, adjusted by complexity 0.5 → multiplier 0.85 → ~510
        target.Should().BeInRange(400, 600, "novella should produce mid-range chunks");
        min.Should().BeGreaterThanOrEqualTo(75);
    }

    [Fact]
    public void ComputeChunkTargets_ShortStory_VerySmallChunks()
    {
        var profile = new DocumentComplexityProfile
        {
            CompositeComplexity = 0.4f,
            TotalChars = 10_000
        };

        var (target, _) = ScrollCommand.ComputeChunkTargets(10_000, IngestDocumentType.Fiction, profile);

        target.Should().BeInRange(200, 350, "short story should get small chunks");
    }

    [Fact]
    public void ComputeChunkTargets_LongNovel_LargerChunks()
    {
        var profile = new DocumentComplexityProfile
        {
            CompositeComplexity = 0.5f,
            TotalChars = 500_000
        };

        var (target, _) = ScrollCommand.ComputeChunkTargets(500_000, IngestDocumentType.Fiction, profile);

        target.Should().BeInRange(700, 1000, "long novel should get larger chunks");
    }

    [Fact]
    public void ComputeChunkTargets_Technical_UsesSmallChunks()
    {
        var profile = new DocumentComplexityProfile
        {
            CompositeComplexity = 0.4f,
            TotalChars = 50_000
        };

        var (target, min) = ScrollCommand.ComputeChunkTargets(50_000, IngestDocumentType.Technical, profile);

        target.Should().BeInRange(200, 400, "technical docs should get small precise chunks");
        min.Should().Be(50);
    }

    [Fact]
    public void ComputeChunkTargets_NullProfile_StillWorks()
    {
        // Without a profile, should use base targets without complexity adjustment
        var (target, _) = ScrollCommand.ComputeChunkTargets(122_000, IngestDocumentType.Fiction, null);

        target.Should().BeInRange(500, 700, "base fiction target without complexity adjustment");
    }

    // ── GetAdaptiveLimits ───────────────────────────────────────────

    [Fact]
    public void GetAdaptiveLimits_WithProfile_ScalesWithComplexity()
    {
        var config = new IngestionConfig();
        var highComplexity = new DocumentComplexityProfile { CompositeComplexity = 0.8f };
        var lowComplexity = new DocumentComplexityProfile { CompositeComplexity = 0.2f };

        var (minHigh, maxHigh, threshHigh) = ScrollCommand.GetAdaptiveLimits(
            60, IngestDocumentType.Fiction, config, highComplexity);
        var (minLow, maxLow, threshLow) = ScrollCommand.GetAdaptiveLimits(
            60, IngestDocumentType.Fiction, config, lowComplexity);

        // Higher complexity → higher threshold (more aggressive dedup)
        threshHigh.Should().BeGreaterThan(threshLow);
        // Min should be the same (both based on 55% of 60)
        minHigh.Should().Be(minLow);
        // Max fraction higher for high complexity
        maxHigh.Should().BeGreaterThanOrEqualTo(maxLow);
    }

    [Fact]
    public void GetAdaptiveLimits_WithProfile_NovellaCase()
    {
        // The novella case from the plan: 60 raw chunks, 0.5 complexity
        var config = new IngestionConfig();
        var profile = new DocumentComplexityProfile { CompositeComplexity = 0.5f };

        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            60, IngestDocumentType.Fiction, config, profile);

        min.Should().Be(33, "55% of 60 = 33");
        max.Should().BeGreaterThanOrEqualTo(38, "max should allow most chunks to survive");
        threshold.Should().BeInRange(0.90f, 0.95f);
    }

    [Fact]
    public void GetAdaptiveLimits_WithoutProfile_LegacyFallback()
    {
        var config = new IngestionConfig();

        // Without profile, should use legacy static table
        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            100, IngestDocumentType.Fiction, config, null);

        // Legacy: Fiction with rawChunkCount > 40 → (30, 120, 0.88f)
        min.Should().Be(30);
        max.Should().Be(120);
        threshold.Should().Be(0.88f);
    }

    [Fact]
    public void GetAdaptiveLimits_ConfigOverrides_StillApply()
    {
        var config = new IngestionConfig
        {
            MinChunksOverride = 5,
            MaxChunksOverride = 25,
            DeduplicationThreshold = 0.95f
        };
        var profile = new DocumentComplexityProfile { CompositeComplexity = 0.5f };

        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            60, IngestDocumentType.Fiction, config, profile);

        min.Should().Be(5);
        max.Should().Be(25);
        threshold.Should().Be(0.95f);
    }

    // ── EstimateChunkSalience (enhanced fiction signals) ─────────────

    [Fact]
    public void EstimateChunkSalience_Fiction_DialogueBoost()
    {
        var heavyDialogue = """
            "Good morning," said Holmes.
            "How do you do?" Watson replied.
            "I have a case for you," Holmes continued.
            "Tell me everything," Watson said eagerly.
            """;
        var noDialogue = "The morning sun cast long shadows across the cobblestone streets of London.";

        var scoreDialogue = ScrollCommand.EstimateChunkSalience(heavyDialogue, 5, 20, IngestDocumentType.Fiction);
        var scorePlain = ScrollCommand.EstimateChunkSalience(noDialogue, 5, 20, IngestDocumentType.Fiction);

        scoreDialogue.Should().BeGreaterThan(scorePlain, "dialogue should boost salience");
    }

    [Fact]
    public void EstimateChunkSalience_Fiction_ChapterBoundary()
    {
        var chapterText = "Chapter Three\n\nThe adventure begins in earnest as our heroes set forth.";
        var normalText = "The adventure begins in earnest as our heroes set forth through the woods.";

        var scoreChapter = ScrollCommand.EstimateChunkSalience(chapterText, 5, 20, IngestDocumentType.Fiction);
        var scoreNormal = ScrollCommand.EstimateChunkSalience(normalText, 5, 20, IngestDocumentType.Fiction);

        scoreChapter.Should().BeGreaterThan(scoreNormal, "chapter boundary should boost salience");
    }

    [Fact]
    public void EstimateChunkSalience_Fiction_CharacterIntroduction()
    {
        var introText = "Mr. Bartholomew Sholto entered the room with a nervous expression on his face.";
        var plainText = "The room was dimly lit with a nervous expression hanging in the air everywhere.";

        var scoreIntro = ScrollCommand.EstimateChunkSalience(introText, 5, 20, IngestDocumentType.Fiction);
        var scorePlain = ScrollCommand.EstimateChunkSalience(plainText, 5, 20, IngestDocumentType.Fiction);

        scoreIntro.Should().BeGreaterThan(scorePlain, "character introduction should boost salience");
    }

    // ── DocumentChunker boundary detection ──────────────────────────

    [Fact]
    public void DocumentChunker_SceneBreak_SplitsAtDivider()
    {
        var text = "The first scene ends here with action.\n\n" +
                   "* * *\n\n" +
                   "The second scene begins with new characters.";

        var hints = new ChunkBoundaryHints { RespectSceneBreaks = true };
        var chunker = new DocumentChunker(targetChunkTokens: 200, minChunkTokens: 5, hints: hints);
        var chunks = chunker.ChunkByStructure(text);

        // Should produce at least 2 chunks (one before, one after the scene break)
        chunks.Should().HaveCountGreaterThanOrEqualTo(2,
            "scene break should force split into separate chunks");
    }

    [Fact]
    public void DocumentChunker_CodeBlock_KeptIntact()
    {
        var text = "# Setup\n\nInstall the package:\n\n" +
                   "```python\n" +
                   "import lucidrag\n" +
                   "client = lucidrag.Client(api_key='test')\n" +
                   "result = client.query('hello')\n" +
                   "print(result)\n" +
                   "```\n\n" +
                   "Then configure the settings for your deployment.";

        var hints = new ChunkBoundaryHints { KeepCodeBlocksTogether = true };
        var chunker = new DocumentChunker(targetChunkTokens: 30, minChunkTokens: 5, hints: hints);
        var chunks = chunker.ChunkByStructure(text);

        // The code block should not be split across chunks
        var codeChunk = chunks.FirstOrDefault(c => c.Content.Contains("```python"));
        codeChunk.Should().NotBeNull("code block should be in a chunk");
        codeChunk!.Content.Should().Contain("print(result)",
            "entire code block should be kept together");
    }

    [Fact]
    public void DocumentChunker_ChapterBoundary_SplitsAtChapter()
    {
        // Use enough text per chapter that MergeSections won't combine them all
        var intro = "Some introductory text for the book that sets the scene. " +
                    "It was a dark and stormy night when our tale begins in earnest. " +
                    "The wind howled through the streets of London as fog crept along the Thames.";
        var ch1 = "Chapter One\n\n" +
                  "The story begins here with our protagonist walking through the fog. " +
                  "He turned the corner and saw a light in the window of the old house. " +
                  "Something stirred in the shadows and he quickened his pace considerably.";
        var ch2 = "Chapter Two\n\n" +
                  "The adventure continues in a new location far from the city. " +
                  "Rolling hills stretched before him as far as the eye could see. " +
                  "A carriage waited at the crossroads with its driver fast asleep.";
        var text = $"{intro}\n\n{ch1}\n\n{ch2}";

        var hints = new ChunkBoundaryHints { RespectChapterBoundaries = true };
        // Small target to ensure chapters don't get merged back together
        var chunker = new DocumentChunker(targetChunkTokens: 60, minChunkTokens: 5, hints: hints);
        var chunks = chunker.ChunkByStructure(text);

        // Should split at chapter boundaries
        chunks.Should().HaveCountGreaterThanOrEqualTo(2,
            "chapter boundaries should create separate sections");
    }

    [Fact]
    public void IsSceneBreak_DetectsVariousPatterns()
    {
        DocumentChunker.IsSceneBreak("***").Should().BeTrue();
        DocumentChunker.IsSceneBreak("* * *").Should().BeTrue();
        DocumentChunker.IsSceneBreak("---").Should().BeTrue();
        DocumentChunker.IsSceneBreak("___").Should().BeTrue();
        DocumentChunker.IsSceneBreak("===").Should().BeTrue();
        DocumentChunker.IsSceneBreak("  ***  ").Should().BeTrue();
        DocumentChunker.IsSceneBreak("This is not a break").Should().BeFalse();
        DocumentChunker.IsSceneBreak("").Should().BeFalse();
    }

    [Fact]
    public void IsDialogueSection_DetectsDialogue()
    {
        var dialogue = """
            "Hello there," said Watson.
            "Good evening," Holmes replied.
            "What brings you here?" asked the inspector.
            """;
        var narrative = """
            The sun set over the Thames. Fog rolled in from the east.
            Street lamps flickered to life one by one along the embankment.
            """;

        DocumentChunker.IsDialogueSection(dialogue).Should().BeTrue();
        DocumentChunker.IsDialogueSection(narrative).Should().BeFalse();
    }

    // ── End-to-End chunk count ──────────────────────────────────────

    [Fact]
    public void EndToEnd_Novella122KB_CorrectChunkCount()
    {
        // Generate synthetic 122KB fiction text
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Chapter One");
        sb.AppendLine();
        var paragraphs = new[]
        {
            "Mr. Sherlock Holmes sat in his armchair, smoking his pipe and reading the morning paper. The fog outside was thick and impenetrable, as it so often was in London during the autumn months. Watson entered the room with a look of mild surprise on his face.",
            "\"Good morning, Watson,\" said Holmes without looking up. \"I trust you had an interesting night at the club?\"",
            "\"How on earth did you know I was at the club?\" Watson exclaimed in astonishment.",
            "\"Elementary,\" Holmes replied with a thin smile. \"The clay on your boots is of a distinctive ochre colour found only in the vicinity of the Diogenes Club. Furthermore, the faint scent of Turkish tobacco clings to your coat.\"",
            "The adventure that was about to unfold would take them across London, from the foggy streets of Whitechapel to the grand houses of Mayfair. It was a case that would test even Holmes's remarkable powers of deduction to their very limits.",
            "Inspector Lestrade arrived shortly after breakfast, looking more harried than usual. His face was drawn, and his eyes betrayed a sleepless night spent poring over evidence that refused to yield its secrets.",
            "\"Mr. Holmes, I need your help,\" Lestrade began, twisting his hat nervously in his hands. \"There has been a most peculiar crime. A man has vanished from a locked room, leaving behind only a strange symbol drawn in chalk upon the floor.\""
        };

        // Repeat to reach ~122KB
        while (sb.Length < 122_000)
        {
            foreach (var p in paragraphs)
            {
                sb.AppendLine(p);
                sb.AppendLine();
                if (sb.Length >= 122_000) break;
            }

            // Add occasional scene breaks and new chapters
            if (sb.Length % 20_000 < 2000)
            {
                sb.AppendLine("* * *");
                sb.AppendLine();
            }

            if (sb.Length % 40_000 < 2000)
            {
                sb.AppendLine("Chapter Two");
                sb.AppendLine();
            }
        }

        var text = sb.ToString();
        var profile = ScrollCommand.ComputeComplexityProfile(text, IngestDocumentType.Fiction);
        var (targetTokens, minTokens) = ScrollCommand.ComputeChunkTargets(text.Length, IngestDocumentType.Fiction, profile);

        var hints = new ChunkBoundaryHints
        {
            RespectSceneBreaks = true,
            KeepDialogueTogether = true,
            RespectChapterBoundaries = true,
            OverlapFraction = 0.12f
        };
        var chunker = new DocumentChunker(
            targetChunkTokens: targetTokens, minChunkTokens: minTokens, hints: hints);
        var chunks = chunker.ChunkByStructure(text);

        // Was ~12 chunks with old system, should now be 40-80+
        chunks.Should().HaveCountGreaterThanOrEqualTo(40,
            $"122KB fiction should produce 40+ chunks (got {chunks.Count}, target={targetTokens}, complexity={profile.CompositeComplexity:F2})");
        chunks.Should().HaveCountLessThanOrEqualTo(200,
            "shouldn't over-fragment into tiny pieces");
    }
}
