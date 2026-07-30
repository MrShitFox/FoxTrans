using FoxTrans.Desktop.Services;
using Xunit;

public sealed class DesktopPreferencesTests
{
    [Fact]
    public void DesktopPreferencesRoundTripSeparatelyFromPipelineConfig()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "desktop-preferences.json");
            var store = new DesktopPreferencesStore(path);
            var expected = new DesktopPreferences(
                DesktopAppearance.Light,
                ReducedMotion: true,
                RememberWindowPlacement: true,
                WindowWidth: 1400,
                WindowHeight: 900,
                WindowX: 120,
                WindowY: 80);

            store.Save(expected);

            Assert.Equal(expected, store.Load());
            Assert.False(File.Exists(Path.Combine(directory, "config.jsonc")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingOrInvalidPreferenceFileFallsBackSafely()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "desktop-preferences.json");
            var store = new DesktopPreferencesStore(path);
            Assert.Equal(new DesktopPreferences(), store.Load());

            File.WriteAllText(path, "{ invalid");
            Assert.Equal(new DesktopPreferences(), store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WindowSizeIsSanitizedToSupportedMinimum()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "desktop-preferences.json");
            var store = new DesktopPreferencesStore(path);
            store.Save(new(WindowWidth: 20, WindowHeight: 10));

            DesktopPreferences loaded = store.Load();

            Assert.Equal(900, loaded.WindowWidth);
            Assert.Equal(620, loaded.WindowHeight);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VrOverlayNumericPreferencesAreClampedLocally()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "desktop-preferences.json");
            var store = new DesktopPreferencesStore(path);
            store.Save(new DesktopPreferences
            {
                VrOverlay = new(
                    WidthMeters: 20,
                    DistanceMeters: 0,
                    PitchDegrees: -200,
                    YawDegrees: 200,
                    VerticalOffsetMeters: -20,
                    Opacity: 2,
                    MaxFramesPerSecond: 100)
            });

            VrOverlayPreferences overlay = store.Load().VrOverlay;

            Assert.Equal(3, overlay.WidthMeters);
            Assert.Equal(0.3, overlay.DistanceMeters);
            Assert.Equal(-60, overlay.PitchDegrees);
            Assert.Equal(90, overlay.YawDegrees);
            Assert.Equal(-1.5, overlay.VerticalOffsetMeters);
            Assert.Equal(1, overlay.Opacity);
            Assert.Equal(12, overlay.MaxFramesPerSecond);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyVrPlacementMigratesToReadableForwardHud()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "desktop-preferences.json");
            var store = new DesktopPreferencesStore(path);
            store.Save(new DesktopPreferences
            {
                VrOverlay = new(
                    WidthMeters: 0.85,
                    DistanceMeters: 1.05,
                    PitchDegrees: -18,
                    YawDegrees: 0,
                    VerticalOffsetMeters: -0.18,
                    Opacity: 0.92)
            });

            VrOverlayPreferences overlay = store.Load().VrOverlay;

            Assert.Equal(1.35, overlay.WidthMeters);
            Assert.Equal(1.15, overlay.DistanceMeters);
            Assert.Equal(0, overlay.PitchDegrees);
            Assert.Equal(-0.05, overlay.VerticalOffsetMeters);
            Assert.Equal(1, overlay.Opacity);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "foxtrans-preference-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
