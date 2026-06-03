using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ManufacturingAI.Infrastructure.LLM;

public sealed class ClaudeLLMService : ILLMService
{
    private const string BaseUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _fallbackApiKey;
    private readonly string _fallbackModel;
    private readonly LlmRuntimeConfig _runtime;

    public ClaudeLLMService(IHttpClientFactory httpClientFactory, LlmRuntimeConfig runtime, IConfiguration config)
    {
        _fallbackApiKey = config["CLAUDE_API_KEY"] ?? string.Empty;
        _fallbackModel  = config["CLAUDE_CHAT_MODEL"] ?? "claude-sonnet-4-6";
        _runtime        = runtime;
        _http           = httpClientFactory.CreateClient(nameof(ClaudeLLMService));
    }

    private string ApiKey => _runtime.ApiKey.NullIfEmpty() ?? _fallbackApiKey;
    private string Model  => _runtime.Model.NullIfEmpty()  ?? _fallbackModel;

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: false);
        using var httpRequest = CreateHttpRequest(body);

        using var response = await _http.SendAsync(httpRequest, ct);
        await EnsureSuccessAsync(response, ct);

        var result = await response.Content.ReadFromJsonAsync<MessagesResponse>(cancellationToken: ct);
        var text = result?.Content?.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;

        return new LLMResponse(
            text,
            result?.Usage?.InputTokens  ?? 0,
            result?.Usage?.OutputTokens ?? 0,
            IsFromFallback: false);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: true);
        using var httpRequest = CreateHttpRequest(body);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

            var json = line["data: ".Length..].Trim();
            if (json == "[DONE]") break;

            StreamEvent? ev = null;
            try { ev = System.Text.Json.JsonSerializer.Deserialize<StreamEvent>(json); }
            catch { continue; }

            if (ev?.Type == "content_block_delta" && ev.Delta?.Type == "text_delta")
            {
                var text = ev.Delta.Text;
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
        }
    }

    private HttpRequestMessage CreateHttpRequest(object body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
        {
            Content = JsonContent.Create(body)
        };
        msg.Headers.Add("x-api-key", ApiKey);
        msg.Headers.Add("anthropic-version", AnthropicVersion);
        return msg;
    }

    private object BuildRequestBody(LLMRequest request, bool stream)
    {
        var messages = new List<object>();
        if (request.History is { Count: > 0 })
        {
            foreach (var msg in request.History)
                messages.Add(new { role = msg.Role, content = msg.Content });
        }
        messages.Add(new { role = "user", content = request.UserMessage });

        return new
        {
            model       = Model,
            max_tokens  = request.MaxTokens,
            system      = string.IsNullOrEmpty(request.SystemPrompt) ? null : request.SystemPrompt,
            messages,
            stream,
            temperature = request.Temperature
        };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                new InvalidOperationException(
                    "Invalid Anthropic API key. Go to Settings → AI Model and update your API key."),
            System.Net.HttpStatusCode.NotFound =>
                new InvalidOperationException(
                    "Claude model not found. Go to Settings → AI Model and select a valid model."),
            System.Net.HttpStatusCode.TooManyRequests =>
                new InvalidOperationException("Anthropic rate limit exceeded. Please try again later."),
            _ =>
                new InvalidOperationException($"Anthropic API error {(int)response.StatusCode}: {body}")
        };
    }

    // ── Response DTOs ────────────────────────────────────────────────────────

    private sealed record MessagesResponse(
        [property: JsonPropertyName("content")] List<ContentBlock>? Content,
        [property: JsonPropertyName("usage")]   UsageInfo? Usage);

    private sealed record ContentBlock(
        [property: JsonPropertyName("type")] string  Type,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record UsageInfo(
        [property: JsonPropertyName("input_tokens")]  int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens);

    private sealed record StreamEvent(
        [property: JsonPropertyName("type")]  string     Type,
        [property: JsonPropertyName("delta")] DeltaBlock? Delta);

    private sealed record DeltaBlock(
        [property: JsonPropertyName("type")] string  Type,
        [property: JsonPropertyName("text")] string? Text);
}
