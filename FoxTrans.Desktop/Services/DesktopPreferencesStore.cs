using System.Text.Json;

namespace FoxTrans.Desktop.Services;

public enum DesktopAppearance
{
    System,
    Dark,
    Light
}

public enum DesktopPage
{
    Live,
    Pipeline,
    Settings
}

public sealed record DesktopPreferences(
    DesktopAppearance Appearance = DesktopAppearance.Dark,
    bool ReducedMotion = false,
    bool LaunchOnLivePage = true,
    bool RememberWindowPlacement = true,
    DesktopPage SelectedPage = DesktopPage.Live,
    double WindowWidth = 1180,
    double WindowHeight = 760,
    int? WindowX = null,
    int? WindowY = null);

public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public DesktopPreferencesStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FoxTrans",
            "desktop-preferences.json");
    }

    public string Path { get; }

    public DesktopPreferences Load()
    {
        try
        {
            if (!File.Exists(Path))
                return new();
            DesktopPreferences? preferences =
                JsonSerializer.Deserialize<DesktopPreferences>(
                    File.ReadAllText(Path),
                    JsonOptions);
            return Sanitize(preferences ?? new());
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = Path + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(Sanitize(preferences), JsonOptions) +
            Environment.NewLine);
        File.Move(temporary, Path, overwrite: true);
    }

    private static DesktopPreferences Sanitize(DesktopPreferences value) =>
        value with
        {
            WindowWidth = Math.Clamp(value.WindowWidth, 900, 5000),
            WindowHeight = Math.Clamp(value.WindowHeight, 620, 3000)
        };
}
