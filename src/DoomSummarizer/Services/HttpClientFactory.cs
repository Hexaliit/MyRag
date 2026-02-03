namespace DoomSummarizer.Services;

/// <summary>
/// Centralised HttpClient creation with standard User-Agent and timeout.
/// </summary>
public static class HttpClientFactory
{
    public const string UserAgent = "MostlyLucid-DoomSummarizer/1.0";

    public static HttpClient CreateDefault()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return client;
    }
}
