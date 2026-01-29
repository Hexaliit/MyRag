namespace DoomSummarizer.Helpers;

public static class FormattingHelpers
{
    public static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    public static string FormatAge(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        return age.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)age.TotalMinutes}m ago",
            < 1440 => $"{(int)age.TotalHours}h ago",
            _ => $"{(int)age.TotalDays}d ago"
        };
    }
}
