namespace DoomSummarizer.Models;

public enum CrawlStage
{
    Crawling,
    Embedding,
    ExtractingEntities,
    Saving,
    PersistingEntities,
    Completed,
    Failed
}

public record CrawlProgressUpdate(
    CrawlStage Stage,
    string Message,
    int Current = 0,
    int Total = 0,
    int ItemsIndexed = 0,
    bool IsComplete = false,
    Exception? Error = null);

public record CrawlSessionResult(
    int PagesVisited,
    int PagesExtracted,
    int PagesSkipped,
    int NewChanged,
    int HttpNotModified,
    int ContentHashCached,
    int RetryCount,
    int FinalAdaptiveDelayMs,
    string KbName);
