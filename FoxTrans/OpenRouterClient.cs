using System.Text;
using System.Text.Json;

public static class OpenRouterClient
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public static async Task<string> TranslateAudioAsync(byte[] wavBytes, AppConfig.ApiConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Key))
        {
            Console.WriteLine("\n[API] Error: API Key is missing in config.json");
            return "";
        }

        try
        {
            string base64Audio = Convert.ToBase64String(wavBytes);

            var payload = new
            {
                model = config.Model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = config.Prompt },
                            new { type = "input_audio", input_audio = new { data = base64Audio, format = "wav" } }
                        }
                    }
                }
            };

            string jsonString = JsonSerializer.Serialize(payload);
            using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint);
            request.Headers.Add("Authorization", $"Bearer {config.Key}");
            request.Content = content;

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[API] Error: {ex.Message}");
            return "";
        }
    }
}