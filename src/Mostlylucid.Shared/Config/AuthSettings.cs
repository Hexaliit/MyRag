namespace Mostlylucid.Shared.Config;

public class AuthSettings : IConfigSection
{
    public string GoogleClientId { get; set; }
    public string GoogleClientSecret { get; set; }

    public string AdminUserGoogleId { get; set; }
    public static string Section => "Auth";
}