using System.Net;
using System.Text;
using System.Text.Json;

public interface IAudioTranslator
{
    Task<string> TranslateAsync(AudioSegment segment, CancellationToken cancellationToken);
}

public sealed class AudioTranslationException : Exception
{
    public AudioTranslationException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class OpenAiAudioTranslator : IAudioTranslator
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig.ApiConfig _config;

    public OpenAiAudioTranslator(HttpClient httpClient, AppConfig.ApiConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<string> TranslateAsync(AudioSegment segment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.Key))
        {
            throw new AudioTranslationException("API key is missing in config.json.");
        }

        byte[] wav = WavPacker.Pack(segment.Pcm.Span, segment.Format);
        var payload = new
        {
            model = _config.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = _config.Prompt },
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = Convert.ToBase64String(wav), format = "wav" }
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.Key);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new AudioTranslationException(
                $"Audio translation request failed: {exception.Message}",
                inner: exception);
        }

        using (response)
        {
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string detail = responseJson.Length <= 1000 ? responseJson : responseJson[..1000];
                throw new AudioTranslationException(
                    $"Audio translation API returned {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}: {detail}",
                    response.StatusCode);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(responseJson);
                string? translation = document.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrWhiteSpace(translation))
                {
                    throw new AudioTranslationException("Audio translation API returned an empty result.");
                }

                return translation.Trim();
            }
            catch (AudioTranslationException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new AudioTranslationException(
                    "Audio translation API returned an unexpected response.",
                    response.StatusCode,
                    exception);
            }
        }
    }
}
