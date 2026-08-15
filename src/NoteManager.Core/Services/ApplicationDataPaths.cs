namespace NoteManager.App.Services;

/// <summary>
/// Well-known local application-data locations used by NoteManager.
/// </summary>
public static class ApplicationDataPaths
{
    public static string WebView2UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteManager",
        "WebView2");
}
