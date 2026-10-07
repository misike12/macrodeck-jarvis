using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using Serilog;

/// <summary>
/// Transcribes audio using NVIDIA NIM's parakeet-tdt speech-to-text cloud endpoint.
/// This is much faster than local whisper.cpp on machines without a GPU:
/// a short clip typically transcribes in 1-5 seconds over the network vs 10-17 seconds locally.
///
/// Uses the same nvapi key as the LLM. Falls back to local whisper if the NIM
/// endpoint is unreachable or returns an error.
/// </summary>
public sealed class NimSpeechTranscriber(RuntimeManager runtime, ILogger logger)
{
    private readonly RuntimeManager _runtime = runtime;
    private readonly ILogger _logger = logger.ForContext<NimSpeechTranscriber>();

    public const string DefaultModel = "nvidia/parakeet-tdt-0.6b-v2";
    public bool IsConfigured { get; private set; }
    public bool IsAvailable { get; private set; }
    public string Model { get; set; } = DefaultModel;

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (baseUrl, apiKey) = ResolveEndpoint();
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
            {
                IsConfigured = false;
                return false;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var url = $"{baseUrl.TrimEnd('/')}/v1/audio/transcriptions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(Model, System.Text.Encoding.UTF8, "text/plain"), "model");
            request.Content = content;

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            IsAvailable = response.IsSuccessStatusCode;
            IsConfigured = true;

            if (IsAvailable)
                _logger.Information("NIM STT endpoint is available at {Url}", baseUrl);
            else
                _logger.Warning("NIM STT endpoint returned {StatusCode}", (int)response.StatusCode);

            return IsAvailable;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.Debug(exception, "NIM STT endpoint probe failed");
            IsConfigured = false;
            IsAvailable = false;
            return false;
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        string wavPath,
        string language,
        CancellationToken cancellationToken)
    {
        var (baseUrl, apiKey) = ResolveEndpoint();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return TranscriptionResult.Failed(
                TranscriptionFailure.NotInstalled,
                "NIM STT is not configured: no API key or base URL");
        }

        if (!File.Exists(wavPath))
        {
            return TranscriptionResult.Failed(
                TranscriptionFailure.NoSpeech,
                $"WAV file not found: {wavPath}");
        }

        var started = DateTimeOffset.UtcNow;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var url = $"{baseUrl.TrimEnd('/')}/v1/audio/transcriptions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(Model, System.Text.Encoding.UTF8, "text/plain"), "model");

            if (!string.IsNullOrWhiteSpace(language) && language != "auto")
            {
                content.Add(new StringContent(language, System.Text.Encoding.UTF8, "text/plain"), "language");
            }

            var wavBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken).ConfigureAwait(false);
            var wavContent = new ByteArrayContent(wavBytes);
            wavContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(wavContent, "file", Path.GetFileName(wavPath));

            request.Content = content;

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.Warning("NIM STT failed: {StatusCode} {Body}", (int)response.StatusCode, errorBody);
                return TranscriptionResult.Failed(
                    TranscriptionFailure.Failed,
                    $"NIM STT HTTP {(int)response.StatusCode}: {errorBody}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonNode.Parse(responseBody);
            var text = result?["text"]?.GetValue<string>() ?? "";

            var duration = DateTimeOffset.UtcNow - started;
            _logger.Information("NIM STT: {Text} in {Ms:F0}ms", text, duration.TotalMilliseconds);

            if (text.Length == 0)
                return TranscriptionResult.Failed(TranscriptionFailure.NoSpeech);

            return TranscriptionResult.Success(text, duration);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TranscriptionResult.Failed(TranscriptionFailure.Failed, "NIM STT request timed out");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.Warning(exception, "NIM STT transcription failed");
            return TranscriptionResult.Failed(TranscriptionFailure.Failed, exception.Message);
        }
    }

    private static (string? BaseUrl, string? ApiKey) ResolveEndpoint()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NIM_STT_BASE_URL")
            ?? Environment.GetEnvironmentVariable("NVIDIA_BASE_URL")
            ?? "https://integrate.api.nvidia.com/v1";

        var apiKey = Environment.GetEnvironmentVariable("NIM_STT_API_KEY")
            ?? Environment.GetEnvironmentVariable("NVIDIA_API_KEY");

        return (baseUrl, apiKey);
    }
}
