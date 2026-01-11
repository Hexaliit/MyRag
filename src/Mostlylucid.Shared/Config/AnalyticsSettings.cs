namespace Mostlylucid.Shared.Config;

public class AnalyticsSettings : IConfigSection
{
    public string? UmamiPath { get; set; }

    public string? WebsiteId { get; set; }

    public string? UmamiScript { get; set; } = string.Empty;
    public static string Section => "Analytics";
}