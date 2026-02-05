using LucidSupport.Services.Ingestion;

namespace LucidSupport.Tests;

public class PatternRegistryTests
{
    [Theory]
    [InlineData("email", null, null, null, "email")]
    [InlineData(null, null, "email", null, "email")]
    [InlineData("text", "user_email", null, null, "email")]
    [InlineData("tel", null, null, null, "phone")]
    [InlineData("text", "phone_number", null, null, "phone")]
    [InlineData(null, null, "cc-number", null, "credit-card")]
    [InlineData("text", "card_number", null, null, "credit-card")]
    [InlineData(null, null, "cc-csc", null, "cvv")]
    [InlineData("text", "cvv_code", null, null, "cvv")]
    [InlineData("text", "expiry_date", null, null, "date-partial")]
    [InlineData(null, null, "postal-code", null, "postal-code")]
    [InlineData("text", "zip_code", null, null, "postal-code")]
    [InlineData("password", null, null, null, "password")]
    [InlineData("url", null, null, null, "url")]
    [InlineData("text", "total_amount", null, null, "currency")]
    [InlineData(null, null, "name", null, "name")]
    [InlineData(null, null, "given-name", null, "name")]
    [InlineData("text", "first_name", null, null, "name")]
    [InlineData(null, null, "address-line1", null, "address")]
    [InlineData("text", "street_address", null, null, "address")]
    [InlineData(null, null, "username", null, "username")]
    [InlineData("search", null, null, null, "search")]
    [InlineData("text", "search_query", null, null, "search")]
    public void Detect_KnownPatterns(string? type, string? name, string? autocomplete, string? placeholder,
        string expected)
    {
        var attrs = new FieldAttributes
        {
            Type = type,
            Name = name,
            Autocomplete = autocomplete,
            Placeholder = placeholder
        };

        Assert.Equal(expected, PatternRegistry.Detect(attrs));
    }

    [Fact]
    public void Detect_NoMatch_ReturnsNull()
    {
        var attrs = new FieldAttributes
        {
            Type = "text",
            Name = "custom_field"
        };

        Assert.Null(PatternRegistry.Detect(attrs));
    }

    [Fact]
    public void Detect_UsernameNotConfusedWithName()
    {
        // "username" should match username pattern, not name pattern
        var attrs = new FieldAttributes
        {
            Type = "text",
            Name = "username"
        };

        Assert.Equal("username", PatternRegistry.Detect(attrs));
    }

    [Fact]
    public void AllPatterns_Contains13Patterns()
    {
        Assert.Equal(13, PatternRegistry.AllPatterns.Count);
    }

    [Fact]
    public void Detect_CaseInsensitive()
    {
        var attrs = new FieldAttributes
        {
            Type = "EMAIL",
            Name = "USER_EMAIL"
        };

        Assert.Equal("email", PatternRegistry.Detect(attrs));
    }
}
