using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

public interface IAudioTranslator
{
    Task<string> TranslateAsync(AudioSegment segment, CancellationToken cancellationToken);
}

public interface IBatchTranscriber
{
    Task<string> TranscribeAsync(AudioSegment segment, CancellationToken cancellationToken);
}

public interface ITextTranslator
{
    Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken);
}

public sealed class OpenAiProviderException : Exception
{
    public OpenAiProviderException(string operation, string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base($"{operation}: {message}", inner)
    {
        Operation = operation;
        StatusCode = statusCode;
    }

    public string Operation { get; }
    public HttpStatusCode? StatusCode { get; }
}

public sealed class OpenAiAudioTranslator(HttpClient httpClient, ResolvedOpenAiAudioSettings settings) : IAudioTranslator
{
    public async Task<string> TranslateAsync(AudioSegment segment, CancellationToken cancellationToken)
    {
        byte[] wav = WavPacker.Pack(segment.Pcm.Span, segment.Format);
        var payload = new
        {
            model = settings.Model,
            messages = new[]
            {
                new { role = "user", content = new object[]
                {
                    new { type = "text", text = settings.Prompt },
                    new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(wav), format = "wav" } }
                }}
            }
        };
        using var request = OpenAiProtocol.Request(settings.Endpoint, settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        string json = await OpenAiProtocol.SendAsync(httpClient, request, "audio translation", cancellationToken);
        return OpenAiProtocol.ChatText(json, "audio translation");
    }
}

public sealed class OpenAiTranscriber(HttpClient httpClient, ResolvedOpenAiTranscriptionSettings settings) : IBatchTranscriber
{
    public async Task<string> TranscribeAsync(AudioSegment segment, CancellationToken cancellationToken)
    {
        byte[] wav = WavPacker.Pack(segment.Pcm.Span, segment.Format);
        using var request = OpenAiProtocol.Request(settings.Endpoint, settings.ApiKey);
        using HttpContent content = settings.RequestFormat switch
        {
            OpenAiTranscriptionRequestFormat.Multipart => CreateMultipartContent(wav),
            OpenAiTranscriptionRequestFormat.Json => CreateJsonContent(wav),
            _ => throw new ArgumentOutOfRangeException(
                nameof(settings.RequestFormat),
                settings.RequestFormat,
                "Unsupported transcription request format.")
        };
        request.Content = content;
        string json = await OpenAiProtocol.SendAsync(httpClient, request, "audio transcription", cancellationToken);
        return OpenAiProtocol.TranscriptionText(json, "audio transcription");
    }

    private MultipartFormDataContent CreateMultipartContent(byte[] wav)
    {
        var form = new MultipartFormDataContent();
        NameValueHeaderValue boundary = form.Headers.ContentType!.Parameters
            .Single(parameter =>
                string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase));
        boundary.Value = boundary.Value!.Trim('"');
        form.Add(new StringContent(settings.Model), "model");
        if (!string.IsNullOrWhiteSpace(settings.Language))
            form.Add(new StringContent(settings.Language), "language");
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        return form;
    }

    private JsonContent CreateJsonContent(byte[] wav) => JsonContent.Create(new
    {
        model = settings.Model,
        input_audio = new
        {
            data = Convert.ToBase64String(wav),
            format = "wav"
        },
        language = settings.Language
    }, options: AppConfig.JsonOptions);
}

public sealed class OpenAiTextTranslator(HttpClient httpClient, ResolvedOpenAiChatSettings settings) : ITextTranslator
{
    public async Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = settings.Model,
            messages = new[]
            {
                new { role = "system", content = settings.Prompt },
                new { role = "user", content = sourceText }
            }
        };
        using var request = OpenAiProtocol.Request(settings.Endpoint, settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        string json = await OpenAiProtocol.SendAsync(httpClient, request, "text translation", cancellationToken);
        return OpenAiProtocol.ChatText(json, "text translation");
    }
}

internal static class OpenAiProtocol
{
    private const int MaxErrorDetailLength = 1000;

    public static HttpRequestMessage Request(Uri endpoint, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    public static async Task<string> SendAsync(HttpClient client, HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException exception) { throw new OpenAiProviderException(operation, $"request failed: {exception.Message}", inner: exception); }
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string detail = body.Length <= MaxErrorDetailLength ? body : body[..MaxErrorDetailLength];
                throw new OpenAiProviderException(operation, $"API returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}", response.StatusCode);
            }
            return body;
        }
    }

    public static string TranscriptionText(string json, string operation)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
                throw new OpenAiProviderException(operation, "API returned an unexpected response.");
            return RequiredText(text.GetString(), operation);
        }
        catch (OpenAiProviderException) { throw; }
        catch (JsonException exception) { throw new OpenAiProviderException(operation, "API returned malformed JSON.", inner: exception); }
    }

    public static string ChatText(string json, string operation)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            string? value = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String).Select(x => x.GetProperty("text").GetString())),
                _ => null
            };
            return RequiredText(value, operation);
        }
        catch (OpenAiProviderException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new OpenAiProviderException(operation, "API returned an unexpected response.", inner: exception); }
    }

    private static string RequiredText(string? text, string operation) =>
        string.IsNullOrWhiteSpace(text) ? throw new OpenAiProviderException(operation, "API returned an empty result.") : text.Trim();
}
