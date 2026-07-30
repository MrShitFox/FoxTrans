using System.Text.Json;

namespace FoxTrans.Desktop.Services;

public enum DesktopAppearance
{
    System,
    Dark,
    Light
}

public sealed record VrOverlayPreferences(
    bool Enabled = true,
    bool ShowHeader = true,
    bool ShowVoiceStatus = true,
    bool ShowWaveform = true,
    bool ShowRecognition = true,
    bool ShowTranslation = true,
    double WidthMeters = 1.35,
    double DistanceMeters = 1.15,
    double PitchDegrees = 0,
    double YawDegrees = 0,
    double VerticalOffsetMeters = -0.05,
    double Opacity = 1,
    int MaxFramesPerSecond = 12);

public sealed record DesktopPreferences(
    DesktopAppearance Appearance = DesktopAppearance.Dark,
    bool ReducedMotion = false,
    bool RememberWindowPlacement = true,
    double WindowWidth = 1180,
    double WindowHeight = 760,
    int? WindowX = null,
    int? WindowY = null)
{
    public VrOverlayPreferences VrOverlay { get; init; } = new();
}

public sealed class DesktopPreferencesStore
{
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
                JsonSerializer.Deserialize(
                    File.ReadAllText(Path),
                    DesktopPreferencesJsonContext.Default.DesktopPreferences);
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
            JsonSerializer.Serialize(
                Sanitize(preferences),
                DesktopPreferencesJsonContext.Default.DesktopPreferences) +
            Environment.NewLine);
        File.Move(temporary, Path, overwrite: true);
    }

    private static DesktopPreferences Sanitize(DesktopPreferences value)
    {
        VrOverlayPreferences overlay = value.VrOverlay ?? new();
        if (UsesLegacyDefaultPlacement(overlay))
        {
            overlay = overlay with
            {
                WidthMeters = 1.35,
                DistanceMeters = 1.15,
                PitchDegrees = 0,
                VerticalOffsetMeters = -0.05,
                Opacity = 1
            };
        }
        return value with
        {
            WindowWidth = Math.Clamp(value.WindowWidth, 900, 5000),
            WindowHeight = Math.Clamp(value.WindowHeight, 620, 3000),
            VrOverlay = overlay with
            {
                WidthMeters = Math.Clamp(overlay.WidthMeters, 0.25, 3),
                DistanceMeters = Math.Clamp(overlay.DistanceMeters, 0.3, 4),
                PitchDegrees = Math.Clamp(overlay.PitchDegrees, -60, 60),
                YawDegrees = Math.Clamp(overlay.YawDegrees, -90, 90),
                VerticalOffsetMeters = Math.Clamp(overlay.VerticalOffsetMeters, -1.5, 1.5),
                Opacity = Math.Clamp(overlay.Opacity, 0.1, 1),
                MaxFramesPerSecond = Math.Clamp(overlay.MaxFramesPerSecond, 5, 12)
            }
        };
    }

    private static bool UsesLegacyDefaultPlacement(VrOverlayPreferences value) =>
        Math.Abs(value.WidthMeters - 0.85) < 0.0001 &&
        Math.Abs(value.DistanceMeters - 1.05) < 0.0001 &&
        Math.Abs(value.PitchDegrees + 18) < 0.0001 &&
        Math.Abs(value.YawDegrees) < 0.0001 &&
        Math.Abs(value.VerticalOffsetMeters - -0.18) < 0.0001 &&
        Math.Abs(value.Opacity - 0.92) < 0.0001;
}
