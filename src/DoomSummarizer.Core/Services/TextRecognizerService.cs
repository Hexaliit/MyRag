using Microsoft.Recognizers.Text.DateTime;
using Microsoft.Recognizers.Text.Number;
using Microsoft.Recognizers.Text.Sequence;

namespace DoomSummarizer.Services;

/// <summary>
///     Extract structured signals from text using Microsoft.Recognizers.Text.
///     Philosophy: Extract everything we can, store it, use what we need.
///     This provides a CONFIRMATION mechanism for sentinel LLM extraction.
/// </summary>
public static class TextRecognizerService
{
    /// <summary>
    ///     Default culture for recognition. Can be overridden per-call.
    ///     Supported: en-us, en-gb, es-es, fr-fr, de-de, pt-br, zh-cn, ja-jp, etc.
    /// </summary>
    public static string DefaultCulture { get; set; } = "en-us";

    /// <summary>
    ///     Extract all recognizable entities from text.
    ///     Returns structured signals that can be stored alongside sentinel extraction.
    /// </summary>
    public static RecognizedSignals ExtractAll(string text, string? culture = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new RecognizedSignals();

        var c = culture ?? DefaultCulture;
        return new RecognizedSignals
        {
            Culture = c,
            DateTimes = ExtractDateTimes(text, c),
            Numbers = ExtractNumbers(text, c),
            Urls = ExtractUrls(text, c),
            PhoneNumbers = ExtractPhoneNumbers(text, c),
            Emails = ExtractEmails(text, c),
            IpAddresses = ExtractIpAddresses(text, c)
        };
    }

    /// <summary>
    ///     Extract date/time expressions - confirms sentinel temporal extraction.
    /// </summary>
    public static List<RecognizedDateTime> ExtractDateTimes(string text, string? culture = null)
    {
        var results = new List<RecognizedDateTime>();
        try
        {
            var recognized = DateTimeRecognizer.RecognizeDateTime(text, culture ?? DefaultCulture);
            foreach (var r in recognized)
            {
                var resolution = r.Resolution;
                if (resolution == null) continue;

                results.Add(new RecognizedDateTime
                {
                    Text = r.Text,
                    Start = r.Start,
                    End = r.End,
                    TypeName = r.TypeName,
                    Resolution = resolution
                });
            }
        }
        catch
        {
            /* Ignore parsing errors */
        }

        return results;
    }

    /// <summary>
    ///     Extract numbers (cardinals, ordinals, percentages, fractions).
    /// </summary>
    public static List<RecognizedNumber> ExtractNumbers(string text, string? culture = null)
    {
        var results = new List<RecognizedNumber>();
        try
        {
            var recognized = NumberRecognizer.RecognizeNumber(text, culture ?? DefaultCulture);
            foreach (var r in recognized)
                if (r.Resolution?.TryGetValue("value", out var val) == true)
                    results.Add(new RecognizedNumber
                    {
                        Text = r.Text,
                        Start = r.Start,
                        Value = val?.ToString(),
                        TypeName = r.TypeName
                    });
        }
        catch
        {
            /* Ignore parsing errors */
        }

        return results;
    }

    /// <summary>
    ///     Extract URLs from text.
    /// </summary>
    public static List<RecognizedSequence> ExtractUrls(string text, string? culture = null)
    {
        var results = new List<RecognizedSequence>();
        try
        {
            var recognized = SequenceRecognizer.RecognizeURL(text, culture ?? DefaultCulture);
            foreach (var r in recognized)
                results.Add(new RecognizedSequence
                {
                    Text = r.Text,
                    Start = r.Start,
                    TypeName = "URL"
                });
        }
        catch
        {
            /* Ignore parsing errors */
        }

        return results;
    }

    /// <summary>
    ///     Extract phone numbers from text.
    /// </summary>
    public static List<RecognizedSequence> ExtractPhoneNumbers(string text, string? culture = null)
    {
        var results = new List<RecognizedSequence>();
        try
        {
            var recognized = SequenceRecognizer.RecognizePhoneNumber(text, culture ?? DefaultCulture);
            foreach (var r in recognized)
                results.Add(new RecognizedSequence
                {
                    Text = r.Text,
                    Start = r.Start,
                    TypeName = "PhoneNumber"
                });
        }
        catch
        {
            /* Ignore parsing errors */
        }

        return results;
    }

    /// <summary>
    ///     Extract email addresses from text.
    /// </summary>
    public static List<RecognizedSequence> ExtractEmails(string text, string? culture = null)
    {
        var results = new List<RecognizedSequence>();
        try
        {
            var recognized = SequenceRecognizer.RecognizeEmail(text, culture ?? DefaultCulture);
            foreach (var r in recognized)
                results.Add(new RecognizedSequence
                {
                    Text = r.Text,
                    Start = r.Start,
                    TypeName = "Email"
                });
        }
        catch
        {
            /* Ignore parsing errors */
        }

        return results;
    }

    /// <summary>
    ///     Extract IP addresses from text.
    /// </summary>
    public static List<RecognizedSequence> ExtractIpAddresses(string text, string? culture = null)
    {
        var results = new List<RecognizedSequence>();
        try
        {
            var recognized = SequenceRecognizer.RecognizeIpAddress(text, culture ?? DefaultCulture);
            foreach (var r in recognized)
                results.Add(new RecognizedSequence
                {
                    Text = r.Text,
                    Start = r.Start,
                    TypeName = "IpAddress"
                });
        }
        catch
        {
            /* Ignore parsing errors */
        }

        return results;
    }

    /// <summary>
    ///     Confirm sentinel's temporal extraction using recognizer output.
    ///     Returns true if recognizer found temporal expressions matching sentinel intent.
    /// </summary>
    public static bool ConfirmTemporalIntent(string query, SentinelIntent? sentinelIntent)
    {
        if (sentinelIntent == null) return false;
        if (!sentinelIntent.RequiresFresh && sentinelIntent.TimeSensitivity == "any" &&
            sentinelIntent.DateRange == null)
            return true; // No temporal intent to confirm

        var dateTimes = ExtractDateTimes(query);

        // If sentinel says temporal but recognizer found nothing, still trust sentinel
        // (recognizer may miss phrases like "recent" which sentinel understands)
        if (dateTimes.Count == 0)
            return true; // No contradiction

        // If both agree there's temporal content, confirmed
        return dateTimes.Count > 0 && (sentinelIntent.RequiresFresh || sentinelIntent.DateRange != null);
    }
}

/// <summary>
///     All signals extracted from text.
/// </summary>
public record RecognizedSignals
{
    /// <summary>
    ///     Culture used for recognition (e.g., "en-us", "en-gb", "de-de").
    /// </summary>
    public string Culture { get; init; } = "en-us";

    public List<RecognizedDateTime> DateTimes { get; init; } = [];
    public List<RecognizedNumber> Numbers { get; init; } = [];
    public List<RecognizedSequence> Urls { get; init; } = [];
    public List<RecognizedSequence> PhoneNumbers { get; init; } = [];
    public List<RecognizedSequence> Emails { get; init; } = [];
    public List<RecognizedSequence> IpAddresses { get; init; } = [];

    public bool HasTemporalSignals => DateTimes.Count > 0;

    public bool HasAnySignals => DateTimes.Count + Numbers.Count + Urls.Count +
        PhoneNumbers.Count + Emails.Count + IpAddresses.Count > 0;
}

/// <summary>
///     Recognized date/time expression with resolution.
/// </summary>
public record RecognizedDateTime
{
    public required string Text { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public string? TypeName { get; init; }
    public IDictionary<string, object> Resolution { get; init; } = new Dictionary<string, object>();
}

/// <summary>
///     Recognized number with resolved value.
/// </summary>
public record RecognizedNumber
{
    public required string Text { get; init; }
    public int Start { get; init; }
    public string? Value { get; init; }
    public string? TypeName { get; init; }
}

/// <summary>
///     Recognized sequence (URL, phone, email, IP).
/// </summary>
public record RecognizedSequence
{
    public required string Text { get; init; }
    public int Start { get; init; }
    public required string TypeName { get; init; }
}