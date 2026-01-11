namespace Mostlylucid.Shared.Config;

public class NewsletterConfig : IConfigSection
{
    public string SchedulerServiceUrl { get; set; } = string.Empty;
    public string AppHostUrl { get; set; } = string.Empty;
    public static string Section => "Newsletter";
}