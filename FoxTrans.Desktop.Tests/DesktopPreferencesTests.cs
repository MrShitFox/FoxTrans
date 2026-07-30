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
