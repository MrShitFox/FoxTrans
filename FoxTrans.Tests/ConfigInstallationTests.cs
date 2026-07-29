using Xunit;

public sealed class ConfigInstallationTests
{
    [Fact]
    public void FreshDirectoryCreatesCanonicalConfigAndSchema()
    {
        string directory = CreateDirectory();
        try
        {
            ConfigLoadResult result = AppConfig.LoadOrCreate(directory);
            Assert.Equal(ConfigLoadState.Created, result.State);
            Assert.True(File.Exists(Path.Combine(directory, "config.jsonc")));
            Assert.Equal(
                AppConfig.GenerateSchema(),
                File.ReadAllText(Path.Combine(directory, "foxtrans.schema.json")));
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "foxtrans.schema.json")),
                File.ReadAllBytes(Path.Combine(
                    directory,
                    "foxtrans.schema.json")));
            Assert.True(ConfigValidator.Validate(result.Config!).IsValid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyConfigMigratesAndPreservesBackup()
    {
        string directory = CreateDirectory();
        try
        {
            string legacy = Path.Combine(directory, "config.json");
            File.WriteAllText(legacy, """
                {
                  "Api": {
                    "Key": "env:TEST_KEY",
                    "Endpoint": "https://example.test/v1/chat/completions",
                    "Model": "model",
                    "Prompt": "prompt"
                  },
                  "Vad": {
                    "MinSpeechFrames": 12,
                    "MinSilenceFrames": 50,
                    "PreRollFrames": 30,
                    "MinPhraseLengthMs": 1200
                  },
                  "Osc": {
                    "IpAddress": "127.0.0.1",
                    "Port": 9000,
                    "EnableTypingIndicator": true
                  }
                }
                """);
            ConfigLoadResult result = AppConfig.LoadOrCreate(directory);
            Assert.Equal(ConfigLoadState.Migrated, result.State);
            Assert.True(File.Exists(Path.Combine(directory, "config.jsonc")));
            Assert.True(File.Exists(Path.Combine(directory, "config.legacy.json")));
            Assert.Equal(
                "https://example.test/v1",
                ((OpenAiChatAudioConfig)result.Config!.EffectivePipeline.Speech!).BaseUrl);
            Assert.Equal("natural-speech", ((WebRtcVadConfig)result.Config.EffectivePipeline.Vad!).Preset);
            Assert.True(ConfigValidator.Validate(result.Config).IsValid);
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
            "foxtrans-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
