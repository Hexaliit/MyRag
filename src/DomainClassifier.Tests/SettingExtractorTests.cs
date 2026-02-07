using DomainClassifier.Narrative.Extractors;

namespace DomainClassifier.Tests;

public class SettingExtractorTests
{
    [Fact]
    public void Extract_LocationAfterVerb_ExtractsLocation()
    {
        var text = "They arrived at Pemberley in the late afternoon.";

        var entities = SettingExtractor.Extract(text);

        Assert.Contains(entities, e =>
            e.EntityType == "setting_location" &&
            e.Text == "Pemberley");
    }

    [Fact]
    public void Extract_LivedInLocation_ExtractsLocation()
    {
        var text = "She lived in London for many years before moving to the country.";

        var entities = SettingExtractor.Extract(text);

        Assert.Contains(entities, e =>
            e.EntityType == "setting_location" &&
            e.Text == "London");
    }

    [Fact]
    public void Extract_TimePeriod_ExtractsYear()
    {
        var text = "The story takes place in the year 1818, a time of great change.";

        var entities = SettingExtractor.Extract(text);

        Assert.Contains(entities, e =>
            e.EntityType == "time_period" &&
            e.Text == "1818");
    }

    [Fact]
    public void Extract_TimePeriodCentury_Extracts()
    {
        var text = "During the 19th century, society underwent tremendous change.";

        var entities = SettingExtractor.Extract(text);

        Assert.Contains(entities, e =>
            e.EntityType == "time_period" &&
            e.Text.Contains("19th century"));
    }

    [Fact]
    public void Extract_AtmosphereTerms_Extracts()
    {
        var text = "The moonlight cast long shadows across the frost-covered garden at midnight.";

        var entities = SettingExtractor.Extract(text);

        Assert.Contains(entities, e => e.EntityType == "atmosphere" && e.Text == "moonlight");
        Assert.Contains(entities, e => e.EntityType == "atmosphere" && e.Text == "frost");
        Assert.Contains(entities, e => e.EntityType == "atmosphere" && e.Text == "midnight");
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        var entities = SettingExtractor.Extract("");

        Assert.Empty(entities);
    }

    [Fact]
    public void Extract_MultipleLocations_ExtractsAll()
    {
        var text = "He traveled to Paris and later journeyed to Rome before returning to London.";

        var entities = SettingExtractor.Extract(text);

        var locations = entities.Where(e => e.EntityType == "setting_location").ToList();
        Assert.True(locations.Count >= 2,
            $"Expected at least 2 locations, got {locations.Count}");
    }

    [Fact]
    public void Extract_MultipleAtmosphereOccurrences_ExtractsAll()
    {
        var text = "The darkness enveloped the forest. As they pressed on through the darkness, " +
                   "they felt the darkness closing in around them once more.";

        var entities = SettingExtractor.Extract(text);

        var darknessEntities = entities.Where(e =>
            e.EntityType == "atmosphere" && e.Text == "darkness").ToList();
        Assert.True(darknessEntities.Count >= 2,
            $"Expected at least 2 darkness occurrences, got {darknessEntities.Count}");
    }

    [Fact]
    public void Extract_VictorianEra_ExtractsTimePeriod()
    {
        var text = "The story is set during the Victorian era, a time of great industrial progress.";

        var entities = SettingExtractor.Extract(text);

        Assert.Contains(entities, e =>
            e.EntityType == "time_period" &&
            e.Text.Contains("Victorian", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_SetInLocation_ExtractsLocation()
    {
        var text = "The novel is set in Cambridge, a city of ancient learning.";

        var entities = SettingExtractor.Extract(text);

        Assert.Contains(entities, e =>
            e.EntityType == "setting_location" &&
            e.Text == "Cambridge");
    }

    [Fact]
    public void Extract_AtmosphereWithCorrectOffsets()
    {
        var text = "The moonlight illuminated the path ahead.";

        var entities = SettingExtractor.Extract(text);

        var moonlight = entities.FirstOrDefault(e => e.Text == "moonlight");
        Assert.NotNull(moonlight);
        Assert.Equal(text.ToLowerInvariant().IndexOf("moonlight"), moonlight.StartOffset);
    }
}
