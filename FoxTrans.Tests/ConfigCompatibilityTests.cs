using Xunit;

public sealed class ConfigCompatibilityTests
{
    [Fact]
    public void ExistingJsonStructureLoadsAllPipelineValues()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "Api": {
                    "Key": "test-key",
                    "Endpoint": "https://example.test/v1/chat/completions",
                    "Model": "test/model",
                    "Prompt": "Translate."
                  },
                  "Vad": {
                    "MinSpeechFrames": 3,
                    "MinSilenceFrames": 7,
                    "PreRollFrames": 11,
                    "MinPhraseLengthMs": 900
                  },
                  "Osc": {
                    "IpAddress": "192.0.2.1",
                    "Port": 1234,
                    "EnableTypingIndicator": false
                  }
                }
                """);

            ConfigLoadResult result = AppConfig.LoadOrCreate(path);

            Assert.False(result.WasCreated);
            Assert.Equal("test-key", result.Config.Api.Key);
            Assert.Equal("https://example.test/v1/chat/completions", result.Config.Api.Endpoint);
            Assert.Equal("test/model", result.Config.Api.Model);
            Assert.Equal("Translate.", result.Config.Api.Prompt);
            Assert.Equal(3, result.Config.Vad.MinSpeechFrames);
            Assert.Equal(7, result.Config.Vad.MinSilenceFrames);
            Assert.Equal(11, result.Config.Vad.PreRollFrames);
            Assert.Equal(900, result.Config.Vad.MinPhraseLengthMs);
            Assert.Equal("192.0.2.1", result.Config.Osc.IpAddress);
            Assert.Equal(1234, result.Config.Osc.Port);
            Assert.False(result.Config.Osc.EnableTypingIndicator);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFileCreatesOldDefaultStructure()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            ConfigLoadResult result = AppConfig.LoadOrCreate(path);
            string json = File.ReadAllText(path);

            Assert.True(result.WasCreated);
            Assert.Contains("\"Api\"", json);
            Assert.Contains("\"Vad\"", json);
            Assert.Contains("\"Osc\"", json);
            Assert.Contains("\"EnableTypingIndicator\": true", json);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"FoxTrans.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
