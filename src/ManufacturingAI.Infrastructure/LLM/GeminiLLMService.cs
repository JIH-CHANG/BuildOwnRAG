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
        var body = BuildRequestBody(request, Model);

        using var response = await PostWithRetryAsync(url, body, ct);
        await EnsureGeminiSuccessAsync(response, Model, ct);

        var result = await response.Content
            .ReadFromJsonAsync<GenerateContentResponse>(cancellationToken: ct);

        var candidate = result?.Candidates?.FirstOrDefault();
        var text = candidate?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
        var inputTokens = result?.UsageMetadata?.PromptTokenCount ?? 0;
        var outputTokens = result?.UsageMetadata?.CandidatesTokenCount ?? 0;

        // Gemini returns HTTP 200 with an empty/truncated answer when the output budget
        // (maxOutputTokens) is exhausted — on thinking models the reasoning tokens can
        // consume it entirely, leaving no visible text. Surface this instead of an empty bubble.
        if (string.Equals(candidate?.FinishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(text))
                throw new InvalidOperationException(
                    "Gemini returned no content because the output limit (MAX_TOKENS) was reached; " +
                    "on thinking models the reasoning tokens can consume the entire maxOutputTokens. " +
                    "Raise MaxTokens in Settings, or switch to a non-thinking model.");

            text += "\n\n[Note: the response may be truncated because the output limit was reached.]";
        }

        return new LLMResponse(text, inputTokens, outputTokens, IsFromFallback: false);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{Model}:streamGenerateContent?key={ApiKey}&alt=sse";
        var body = BuildRequestBody(request, Model);

        using var response = await SendStreamWithRetryAsync(url, body, ct);
        await EnsureGeminiSuccessAsync(response, Model, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        var produced = false;
        string? finishReason = null;

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

            var candidate = chunk?.Candidates?.FirstOrDefault();
            finishReason = candidate?.FinishReason ?? finishReason;

            var text = candidate?.Content?.Parts?.FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                produced = true;
                yield return text;
            }
        }

        // Same MAX_TOKENS guard as CompleteAsync: if thinking consumed the whole budget
        // the stream yields no text, which would otherwise render as an empty bubble.
        if (!produced && string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            yield return "[Gemini returned no content: output limit (MAX_TOKENS) reached. " +
                         "Raise MaxTokens in Settings, or switch to a non-thinking model.]";
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

    private static object BuildRequestBody(LLMRequest request, string model)
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

        var systemInstruction = string.IsNullOrEmpty(request.SystemPrompt) ? null : new
        {
            parts = new[] { new { text = request.SystemPrompt } }
        };

        // On thinking models the reasoning tokens count against maxOutputTokens, so we
        // disable thinking to guarantee the budget is spent on the visible answer.
        var thinkingBudget = ResolveThinkingBudget(model);
        object generationConfig = thinkingBudget is int budget
            ? new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxTokens,
                thinkingConfig = new { thinkingBudget = budget }
            }
            : new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxTokens
            };

        return new { contents, systemInstruction, generationConfig };
    }

    // thinkingBudget = 0 disables thinking. Only the Gemini 2.5+/3.x "flash" family
    // supports disabling it; older flash models (1.5 / 2.0) don't think, and "pro"
    // models reject a zero budget, so we omit thinkingConfig (null) for those.
    private static int? ResolveThinkingBudget(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("flash") && !m.Contains("1.5") && !m.Contains("2.0"))
            return 0;
        return null;
    }

    // ── Response DTOs ────────────────────────────────────────────────────────

    private sealed record GenerateContentResponse(
        [property: JsonPropertyName("candidates")] List<Candidate>? Candidates,
        [property: JsonPropertyName("usageMetadata")] UsageMetadata? UsageMetadata);

    private sealed record Candidate(
        [property: JsonPropertyName("content")] ContentBlock? Content,
        [property: JsonPropertyName("finishReason")] string? FinishReason);

    private sealed record ContentBlock(
        [property: JsonPropertyName("parts")] List<Part>? Parts);

    private sealed record Part(
        [property: JsonPropertyName("text")] string? Text);

    private sealed record UsageMetadata(
        [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount,
        [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount);
}
