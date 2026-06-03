using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ManufacturingAI.Infrastructure.LLM;

public sealed class GeminiLLMService : ILLMService
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private readonly HttpClient _http;
    private readonly string _fallbackApiKey;
    private readonly string _fallbackModel;
    private readonly LlmRuntimeConfig _runtime;

    public GeminiLLMService(IConfiguration config, IHttpClientFactory httpClientFactory, LlmRuntimeConfig runtime)
    {
        _fallbackApiKey = config["GEMINI_API_KEY"] ?? string.Empty;
        _fallbackModel = config["GEMINI_CHAT_MODEL"] ?? "gemini-3.1-flash-lite";
        _runtime = runtime;
        _http = httpClientFactory.CreateClient(nameof(GeminiLLMService));
    }

    private string ApiKey => _runtime.ApiKey.NullIfEmpty() ?? _fallbackApiKey;
    private string Model  => _runtime.Model.NullIfEmpty()  ?? _fallbackModel;

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{Model}:generateContent?key={ApiKey}";
        var body = BuildRequestBody(request);

        using var response = await PostWithRetryAsync(url, body, ct);
        await EnsureGeminiSuccessAsync(response, Model, ct);

        var result = await response.Content
            .ReadFromJsonAsync<GenerateContentResponse>(cancellationToken: ct);

        var text = result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
        var inputTokens = result?.UsageMetadata?.PromptTokenCount ?? 0;
        var outputTokens = result?.UsageMetadata?.CandidatesTokenCount ?? 0;

        return new LLMResponse(text, inputTokens, outputTokens, IsFromFallback: false);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{Model}:streamGenerateContent?key={ApiKey}&alt=sse";
        var body = BuildRequestBody(request);

        using var response = await SendStreamWithRetryAsync(url, body, ct);
        await EnsureGeminiSuccessAsync(response, Model, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var json = line["data: ".Length..].Trim();
            if (json == "[DONE]") break;

            GenerateContentResponse? chunk = null;
            try { chunk = System.Text.Json.JsonSerializer.Deserialize<GenerateContentResponse>(json); }
            catch { continue; }

            var text = chunk?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    // Retries on 429 with exponential backoff (2s → 4s → 8s), matching GeminiEmbeddingService.
    private async Task<HttpResponseMessage> PostWithRetryAsync(string url, object body, CancellationToken ct)
    {
        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            response = await _http.PostAsJsonAsync(url, body, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                break;
        }
        return response;
    }

    // Same backoff for the streaming endpoint; the request message is rebuilt each attempt.
    private async Task<HttpResponseMessage> SendStreamWithRetryAsync(string url, object body, CancellationToken ct)
    {
        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                break;
        }
        return response;
    }

    private static async Task EnsureGeminiSuccessAsync(HttpResponseMessage response, string model, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw response.StatusCode switch
        {
            System.Net.HttpStatusCode.NotFound =>
                new InvalidOperationException(
                    $"Gemini model '{model}' not found. Go to Settings → AI Model and select a valid model."),
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                new InvalidOperationException(
                    "Invalid Gemini API key. Go to Settings → AI Model and update your API key."),
            System.Net.HttpStatusCode.TooManyRequests =>
                new InvalidOperationException("Gemini rate limit exceeded. Please try again later."),
            _ =>
                new InvalidOperationException(
                    $"Gemini API error {(int)response.StatusCode}: {body}")
        };
    }

    private static object BuildRequestBody(LLMRequest request)
    {
        var contents = new List<object>();

        if (request.History is { Count: > 0 })
        {
            foreach (var msg in request.History)
            {
                // Gemini uses "model" not "assistant"
                var role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
                contents.Add(new { role, parts = new[] { new { text = msg.Content } } });
            }
        }

        contents.Add(new { role = "user", parts = new[] { new { text = request.UserMessage } } });

        return new
        {
            contents,
            systemInstruction = string.IsNullOrEmpty(request.SystemPrompt) ? null : new
            {
                parts = new[] { new { text = request.SystemPrompt } }
            },
            generationConfig = new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxTokens
            }
        };
    }

    // ── Response DTOs ────────────────────────────────────────────────────────

    private sealed record GenerateContentResponse(
        [property: JsonPropertyName("candidates")] List<Candidate>? Candidates,
        [property: JsonPropertyName("usageMetadata")] UsageMetadata? UsageMetadata);

    private sealed record Candidate(
        [property: JsonPropertyName("content")] ContentBlock? Content);

    private sealed record ContentBlock(
        [property: JsonPropertyName("parts")] List<Part>? Parts);

    private sealed record Part(
        [property: JsonPropertyName("text")] string? Text);

    private sealed record UsageMetadata(
        [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount,
        [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount);
}
