namespace DoomSummarizer.Helpers;

/// <summary>
/// Zero-allocation word counter. Counts whitespace-separated tokens
/// without splitting into a string array.
/// </summary>
public static class WordCounter
{
    /// <summary>
    /// Count words in the given text without allocating.
    /// Words are separated by spaces, tabs, newlines, or carriage returns.
    /// </summary>
    public static int Count(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;

        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r')
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Count words in the given string without allocating.
    /// </summary>
    public static int Count(string? text)
        => text is null ? 0 : Count(text.AsSpan());
}
