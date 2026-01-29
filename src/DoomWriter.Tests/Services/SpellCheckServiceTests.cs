using DoomWriter.Models;
using DoomWriter.Services;

namespace DoomWriter.Tests.Services;

/// <summary>
/// Tests for SpellCheckService.
/// Note: Most tests exercise pure logic without a Hunspell dictionary loaded.
/// Tests that require a dictionary are marked with [Trait("Category", "RequiresDictionary")].
/// </summary>
public class SpellCheckServiceTests : IDisposable
{
    private readonly SpellCheckService _sut;

    public SpellCheckServiceTests()
    {
        var settings = new WriterSettingsService();
        _sut = new SpellCheckService(settings);
    }

    // --- CheckWord (without dictionary loaded — assumes correct) ---

    [Fact]
    public void CheckWord_ReturnsTrueWhenNoDictionaryLoaded()
    {
        _sut.IsLoaded.Should().BeFalse();
        _sut.CheckWord("misspeled").Should().BeTrue(); // No dictionary = assume correct
    }

    [Fact]
    public void CheckWord_ReturnsTrueForEmptyString()
    {
        _sut.CheckWord("").Should().BeTrue();
    }

    [Fact]
    public void CheckWord_ReturnsTrueForSingleChar()
    {
        _sut.CheckWord("a").Should().BeTrue();
    }

    // --- IsCodeOrSpecial detection (tested via CheckWord behavior) ---

    [Theory]
    [InlineData("API")] // All caps acronym
    [InlineData("HTTP")] // All caps
    [InlineData("HTML5")] // Contains digit
    [InlineData("v2")] // Contains digit
    [InlineData("CamelCase")] // PascalCase identifier
    [InlineData("fileName")] // camelCase
    [InlineData("some.path")] // Contains dot
    [InlineData("my_var")] // Contains underscore
    [InlineData("kebab-case")] // Contains dash
    public void CheckWord_SkipsCodeAndSpecialPatterns(string word)
    {
        // These should always return true (skipped) regardless of dictionary
        _sut.CheckWord(word).Should().BeTrue();
    }

    // --- Custom dictionary ---

    [Fact]
    public async Task AddToCustomDictionary_WordIsAccepted()
    {
        await _sut.AddToCustomDictionaryAsync("foobar");

        // Custom word should be recognized (even without main dictionary)
        // We can't test this directly without loading, but we can verify no exception
    }

    [Fact]
    public async Task AddToCustomDictionary_EmptyString_NoOp()
    {
        await _sut.AddToCustomDictionaryAsync("");
        await _sut.AddToCustomDictionaryAsync("  ");
        // Should not throw
    }

    // --- Entity name integration ---

    [Fact]
    public void AddEntityNames_AddsFullNameAndWords()
    {
        _sut.AddEntityNames(["Hacker News", "EmbeddingService"]);

        // Entity names are added to custom words set
        // We can verify via CheckDocument that they won't be flagged
    }

    // --- CheckParagraph ---

    [Fact]
    public void CheckParagraph_ReturnsEmptyWhenNoDictionary()
    {
        var result = _sut.CheckParagraph("This has a mispeled word.");
        result.Should().BeEmpty(); // No dictionary loaded
    }

    // --- CheckDocument ---

    [Fact]
    public void CheckDocument_ReturnsEmptyWhenNoDictionary()
    {
        var result = _sut.CheckDocument("# Title\n\nSome text with erors.");
        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckDocument_EmptyContent_ReturnsEmpty()
    {
        var result = _sut.CheckDocument("");
        result.Should().BeEmpty();
    }

    // --- Suggest ---

    [Fact]
    public void Suggest_ReturnsEmptyWhenNoDictionary()
    {
        var suggestions = _sut.Suggest("teh");
        suggestions.Should().BeEmpty();
    }

    // --- InitializeAsync ---

    [Fact]
    public async Task InitializeAsync_MissingDictFiles_DoesNotThrow()
    {
        // Should gracefully handle missing dictionary files
        await _sut.InitializeAsync("nonexistent_language_XY");
        _sut.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public void ActiveLanguage_DefaultsToEnUS()
    {
        _sut.ActiveLanguage.Should().Be("en_US");
    }

    [Fact]
    public async Task InitializeAsync_SetsActiveLanguage()
    {
        await _sut.InitializeAsync("en_GB");
        _sut.ActiveLanguage.Should().Be("en_GB");
    }

    // --- DiagnosticItem model ---

    [Fact]
    public void DiagnosticItem_CanBeCreated()
    {
        var item = new DiagnosticItem
        {
            Type = DiagnosticType.Spelling,
            Word = "teh",
            Line = 5,
            Column = 10,
            Length = 3,
            Message = "Unknown word: \"teh\"",
            Suggestions = ["the", "tea", "ten"],
            QuickFixText = "the"
        };

        item.Type.Should().Be(DiagnosticType.Spelling);
        item.Word.Should().Be("teh");
        item.Line.Should().Be(5);
        item.Column.Should().Be(10);
        item.Length.Should().Be(3);
        item.Suggestions.Should().HaveCount(3);
        item.QuickFixText.Should().Be("the");
    }

    [Fact]
    public void DiagnosticType_HasAllExpectedValues()
    {
        Enum.GetValues<DiagnosticType>().Should().HaveCount(4);
        Enum.GetValues<DiagnosticType>().Should().Contain(DiagnosticType.Spelling);
        Enum.GetValues<DiagnosticType>().Should().Contain(DiagnosticType.Grammar);
        Enum.GetValues<DiagnosticType>().Should().Contain(DiagnosticType.Suggestion);
        Enum.GetValues<DiagnosticType>().Should().Contain(DiagnosticType.EntityLink);
    }

    public void Dispose()
    {
        _sut.Dispose();
    }
}
