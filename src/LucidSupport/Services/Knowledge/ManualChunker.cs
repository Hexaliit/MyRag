namespace LucidSupport.Services.Knowledge;

/// <summary>
///     Splits markdown/text documents into overlapping chunks, preserving section context.
/// </summary>
public sealed class ManualChunker
{
    private const int TargetChunkSize = 1500;
    private const double OverlapRatio = 0.1;

    /// <summary>
    ///     Split text into chunks. Uses markdown headings (##, ###) as primary boundaries,
    ///     falling back to paragraph boundaries (\n\n).
    /// </summary>
    public IEnumerable<(string Text, string? Section)> Chunk(string text)
    {
        var sections = SplitBySections(text);

        foreach (var (sectionTitle, sectionText) in sections)
        {
            if (sectionText.Length <= TargetChunkSize)
            {
                if (!string.IsNullOrWhiteSpace(sectionText))
                    yield return (sectionText.Trim(), sectionTitle);
                continue;
            }

            // Split large sections by paragraphs
            foreach (var chunk in SplitByParagraphs(sectionText))
            {
                if (!string.IsNullOrWhiteSpace(chunk))
                    yield return (chunk.Trim(), sectionTitle);
            }
        }
    }

    private static List<(string? Title, string Text)> SplitBySections(string text)
    {
        var sections = new List<(string? Title, string Text)>();
        var lines = text.Split('\n');
        string? currentTitle = null;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ") || line.StartsWith("### "))
            {
                // Flush previous section
                if (currentLines.Count > 0)
                {
                    sections.Add((currentTitle, string.Join('\n', currentLines)));
                    currentLines.Clear();
                }

                currentTitle = line.TrimStart('#', ' ');
                continue;
            }

            currentLines.Add(line);
        }

        // Flush last section
        if (currentLines.Count > 0)
            sections.Add((currentTitle, string.Join('\n', currentLines)));

        return sections;
    }

    private static IEnumerable<string> SplitByParagraphs(string text)
    {
        var paragraphs = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        var overlap = (int)(TargetChunkSize * OverlapRatio);
        var buffer = new List<string>();
        var bufferLen = 0;

        foreach (var para in paragraphs)
        {
            if (bufferLen + para.Length > TargetChunkSize && buffer.Count > 0)
            {
                // Yield current buffer
                yield return string.Join("\n\n", buffer);

                // Keep last paragraph(s) as overlap up to overlap budget
                var overlapBuffer = new List<string>();
                var overlapLen = 0;
                for (var i = buffer.Count - 1; i >= 0; i--)
                {
                    if (overlapLen + buffer[i].Length > overlap) break;
                    overlapBuffer.Insert(0, buffer[i]);
                    overlapLen += buffer[i].Length;
                }

                buffer.Clear();
                buffer.AddRange(overlapBuffer);
                bufferLen = overlapLen;
            }

            buffer.Add(para.Trim());
            bufferLen += para.Length;
        }

        if (buffer.Count > 0)
            yield return string.Join("\n\n", buffer);
    }
}
