using System.Text.Json;

public class AppConfig
{
    public ApiConfig Api { get; set; } = new ApiConfig();
    public VadConfig Vad { get; set; } = new VadConfig();
    public OscConfig Osc { get; set; } = new OscConfig();

    public class ApiConfig
    {
        public string Key { get; set; } = "";
        public string Endpoint { get; set; } = "https://openrouter.ai/api/v1/chat/completions";
        public string Model { get; set; } = "google/gemini-2.5-flash";
        public string Prompt { get; set; } = "Translate this audio to English. Reply ONLY with the final translated text, no quotes or explanations.";
    }

    public class VadConfig
    {
        public int MinSpeechFrames { get; set; } = 12;
        public int MinSilenceFrames { get; set; } = 50;
        public int PreRollFrames { get; set; } = 30;
        public int MinPhraseLengthMs { get; set; } = 1200;
    }

    public class OscConfig
    {
        public string IpAddress { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 9000;
        public bool EnableTypingIndicator { get; set; } = true;
    }

    public static AppConfig Load(string path = "config.json")
    {
        if (!File.Exists(path))
        {
            var defaultConfig = new AppConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(defaultConfig, options));
            Console.WriteLine($"[SYS] Created default {path}. Please set your API key and restart.");
            Environment.Exit(0);
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }
}