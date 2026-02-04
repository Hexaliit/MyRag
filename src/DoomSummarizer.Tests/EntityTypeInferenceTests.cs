using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for EntityProfileService.InferEntityType - static method that doesn't require ONNX model.
///     These tests can run in CI without any model downloads.
/// </summary>
public class EntityTypeInferenceTests
{
    [Theory]
    [InlineData("OpenAI", "ORG")]
    [InlineData("Google", "ORG")]
    [InlineData("Microsoft", "ORG")]
    [InlineData("NASA", "ORG")]
    [InlineData("FBI", "ORG")]
    [InlineData("MIT", "ORG")]
    [InlineData("Acme Corp", "ORG")]
    [InlineData("Apple Inc", "ORG")]
    [InlineData("The New York Times", "ORG")]
    public void InferEntityType_DetectsOrganizations(string entityName, string expectedType)
    {
        var result = EntityProfileService.InferEntityType(entityName);
        Assert.Equal(expectedType, result);
    }

    [Theory]
    [InlineData("California", "LOC")]
    [InlineData("USA", "LOC")]
    [InlineData("London", "LOC")]
    [InlineData("Silicon Valley", "LOC")]
    [InlineData("EU", "LOC")]
    public void InferEntityType_DetectsLocations(string entityName, string expectedType)
    {
        var result = EntityProfileService.InferEntityType(entityName);
        Assert.Equal(expectedType, result);
    }

    [Theory]
    [InlineData("Sam Altman", "PER")]
    [InlineData("Elon Musk", "PER")]
    [InlineData("John Smith", "PER")]
    [InlineData("Mary Jane Watson", "PER")]
    public void InferEntityType_DetectsPersons(string entityName, string expectedType)
    {
        var result = EntityProfileService.InferEntityType(entityName);
        Assert.Equal(expectedType, result);
    }

    [Theory]
    [InlineData("artificial intelligence", "MISC")]
    [InlineData("machine learning", "MISC")]
    [InlineData("GPT-4", "MISC")]
    [InlineData("", "MISC")]
    public void InferEntityType_DefaultsToMisc(string entityName, string expectedType)
    {
        var result = EntityProfileService.InferEntityType(entityName);
        Assert.Equal(expectedType, result);
    }
}