using LucidSupport.Services.Ingestion;

namespace LucidSupport.Tests;

public class SupportMarkdownParserTests
{
    private const string FullDocument = """
        ---
        page_id: checkout-payment
        url_pattern: /checkout/payment*
        title: Payment Page
        learned: 2026-02-04T12:00:00Z
        site: myapp.example.com
        layouts:
          mobile: { max: 767, columns: 1 }
          desktop: { min: 1024, columns: 2, note: "form left, summary right" }
        nav:
          back: { label: "Back to Shipping", selector: "#back-link" }
          next: { label: "Pay Now", selector: "#pay-btn" }
        flow: checkout
        step: 2
        prev: checkout-shipping
        ---

        # Payment Page

        Collects payment details.

        ## Fields

        ### [#card-number] Card Number
        - type: text
        - label: Card number
        - placeholder: 1234 5678 9012 3456
        - pattern: credit-card
        - validation:
          - client: required, pattern `[0-9\s]{13,19}`
          - server: Luhn algorithm, BIN lookup
        - errors:
          - required: "Card number is required"
          - pattern: "Please enter a valid card number"
        - help: Enter your 16-digit card number without spaces.

        ### [#expiry] Expiry Date
        - type: text
        - label: MM/YY
        - pattern: date-partial
        - validation:
          - client: required, pattern `(0[1-9]|1[0-2])\/\d{2}`
          - server: must be future date
        - errors:
          - expired: "This card has expired"
          - format: "Please use MM/YY format"
        - help: The expiration date on your card.

        ## Conditions

        > when: [#card-number].error AND user.attempts > 2
        > suggest: Having trouble? Try a different payment method.
        > highlight: #card-number

        > when: page.idle > 30s AND form.incomplete
        > suggest: Need help completing your payment?

        ## Topics
        - "What payment methods do you accept?" → accepted-payment-methods
        - "Is my payment secure?" → security-and-encryption
        """;

    [Fact]
    public void Parse_FullDocument_ExtractsFrontmatter()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);

        Assert.Equal("checkout-payment", model.PageId);
        Assert.Equal("/checkout/payment*", model.UrlPattern);
        Assert.Equal("Payment Page", model.Title);
        Assert.Equal("myapp.example.com", model.Site);
        Assert.Equal("checkout", model.Flow);
        Assert.Equal(2, model.Step);
        Assert.Equal("checkout-shipping", model.Prev);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsLayouts()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);

        Assert.Equal(2, model.Layouts.Count);

        var mobile = model.Layouts.First(l => l.Name == "mobile");
        Assert.Null(mobile.MinWidth);
        Assert.Equal(767, mobile.MaxWidth);
        Assert.Equal(1, mobile.Columns);

        var desktop = model.Layouts.First(l => l.Name == "desktop");
        Assert.Equal(1024, desktop.MinWidth);
        Assert.Equal(2, desktop.Columns);
        Assert.Equal("form left, summary right", desktop.Note);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsNav()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);

        Assert.Equal(2, model.Nav.Count);
        Assert.Equal("Back to Shipping", model.Nav["back"].Label);
        Assert.Equal("#back-link", model.Nav["back"].Selector);
        Assert.Equal("Pay Now", model.Nav["next"].Label);
        Assert.Equal("#pay-btn", model.Nav["next"].Selector);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsDescription()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);

        Assert.NotNull(model.Description);
        Assert.Contains("Collects payment details", model.Description);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsFields()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);

        Assert.Equal(2, model.Fields.Count);

        var card = model.Fields.First(f => f.Selector == "#card-number");
        Assert.Equal("Card Number", card.Label);
        Assert.Equal("text", card.Type);
        Assert.Equal("Card number", card.DisplayLabel);
        Assert.Equal("1234 5678 9012 3456", card.Placeholder);
        Assert.Equal("credit-card", card.Pattern);
        Assert.Equal("Enter your 16-digit card number without spaces.", card.Help);

        var expiry = model.Fields.First(f => f.Selector == "#expiry");
        Assert.Equal("Expiry Date", expiry.Label);
        Assert.Equal("date-partial", expiry.Pattern);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsValidation()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);
        var card = model.Fields.First(f => f.Selector == "#card-number");

        Assert.Single(card.ClientValidation);
        Assert.Contains("required", card.ClientValidation[0]);

        Assert.Single(card.ServerValidation);
        Assert.Contains("Luhn", card.ServerValidation[0]);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsErrors()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);
        var card = model.Fields.First(f => f.Selector == "#card-number");

        Assert.Equal(2, card.Errors.Count);
        Assert.Equal("Card number is required", card.Errors["required"]);
        Assert.Equal("Please enter a valid card number", card.Errors["pattern"]);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsConditions()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);

        Assert.Equal(2, model.Conditions.Count);

        Assert.Equal("[#card-number].error AND user.attempts > 2", model.Conditions[0].When);
        Assert.Equal("Having trouble? Try a different payment method.", model.Conditions[0].Suggest);
        Assert.Equal("#card-number", model.Conditions[0].Highlight);

        Assert.Equal("page.idle > 30s AND form.incomplete", model.Conditions[1].When);
        Assert.Null(model.Conditions[1].Highlight);
    }

    [Fact]
    public void Parse_FullDocument_ExtractsTopics()
    {
        var model = SupportMarkdownParser.Parse(FullDocument);

        Assert.Equal(2, model.Topics.Count);
        Assert.Equal("What payment methods do you accept?", model.Topics[0].Question);
        Assert.Equal("accepted-payment-methods", model.Topics[0].ArticleId);
        Assert.Equal("Is my payment secure?", model.Topics[1].Question);
        Assert.Equal("security-and-encryption", model.Topics[1].ArticleId);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyModel()
    {
        var model = SupportMarkdownParser.Parse("");

        Assert.Equal(string.Empty, model.PageId);
        Assert.Empty(model.Fields);
        Assert.Empty(model.Conditions);
        Assert.Empty(model.Topics);
    }

    [Fact]
    public void Parse_NoFrontmatter_HandlesGracefully()
    {
        var markdown = """
            # Simple Page

            ## Fields

            ### [#name] Name
            - type: text
            - help: Enter your name.
            """;

        var model = SupportMarkdownParser.Parse(markdown);

        Assert.Equal(string.Empty, model.PageId);
        Assert.Single(model.Fields);
        Assert.Equal("#name", model.Fields[0].Selector);
        Assert.Equal("Enter your name.", model.Fields[0].Help);
    }

    [Fact]
    public void Parse_MissingSections_HandlesGracefully()
    {
        var markdown = """
            ---
            page_id: simple
            url_pattern: /simple
            title: Simple
            ---

            Just a description, no fields or conditions.
            """;

        var model = SupportMarkdownParser.Parse(markdown);

        Assert.Equal("simple", model.PageId);
        Assert.Empty(model.Fields);
        Assert.Empty(model.Conditions);
        Assert.Empty(model.Topics);
        Assert.Contains("Just a description", model.Description);
    }
}
