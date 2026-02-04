using DoomWriter.Models;

namespace DoomWriter.Services;

/// <summary>
///     Manages loading and saving WriterConfig.
///     Singleton service providing app-wide settings access.
/// </summary>
public class WriterSettingsService
{
    public WriterSettingsService()
    {
        Config = WriterConfig.Load();
    }

    public WriterConfig Config { get; private set; }

    public void Save()
    {
        Config.Save();
    }

    public void Reload()
    {
        Config = WriterConfig.Load();
    }
}