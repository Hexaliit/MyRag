namespace Mostlylucid.Shared.Config;

public class AnnouncementConfig : IConfigSection
{
    /// <summary>
    ///     API token for authentication when updating announcements
    ///     Should be a long random string stored in .env / appsettings
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    public static string Section => "Announcement";
}