using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Caching;
using ManufacturingAI.Infrastructure.LLM;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ManufacturingAI.API.Controllers;

public record TenantAiSettingsResponse(string Provider, string Model, string EmbeddingProvider, string EmbeddingModel, bool ApiKeySet, string ApiKeyMasked, bool EmbeddingApiKeySet, string EmbeddingApiKeyMasked, string RetrievalMode);
public record UpdateAiSettingsRequest(string? Provider, string? Model, string? EmbeddingProvider, string? EmbeddingModel, string? ApiKey, string? EmbeddingApiKey, string? RetrievalMode);
public record OllamaModelResponse(string Name, long Size);
public record SystemPromptResponse(string SystemPrompt, string DefaultPrompt);
public record UpdateSystemPromptRequest(string? SystemPrompt);

[ApiController]
[Route("api/v1/tenant")]
[Authorize(Policy = "CanManageUsers")]
public class TenantSettingsController(
    IRepository<Tenant> tenantRepository,
    IHttpClientFactory httpClientFactory,
    LlmRuntimeConfig runtimeConfig,
    ICacheService cache,
    IConfiguration config) : ControllerBase
{
    [HttpGet("settings/ai-model")]
    public async Task<ActionResult<ApiResponse<TenantAiSettingsResponse>>> GetAiModel(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
            return NotFound(this.ApiFail("Tenant not found."));

        var provider = NormalizeProvider(
            NullIfEmpty(tenant.Settings.LLMProvider)
            ?? config["Llm:Provider"]
            ?? "OpenAI");

        var model = NullIfEmpty(tenant.Settings.LLMModel)
            ?? config["Llm:Model"]
            ?? config["GEMINI_CHAT_MODEL"]
            ?? string.Empty;

        var embeddingProvider = NormalizeProvider(
            NullIfEmpty(tenant.Settings.EmbeddingProvider)
            ?? NullIfEmpty(tenant.Settings.LLMProvider)
            ?? config["Llm:Provider"]
            ?? "OpenAI");

        var embeddingModel = NullIfEmpty(tenant.Settings.EmbeddingModel)
            ?? config["GEMINI_EMBEDDING_MODEL"]
            ?? string.Empty;

        // API key stored as plaintext — show masked version for display only
        var storedKey = tenant.Settings.LLMApiKey;
        var apiKeySet = !string.IsNullOrEmpty(storedKey);
        var apiKeyMasked = apiKeySet && storedKey.Length > 4
            ? "••••••••" + storedKey[^4..]
            : (apiKeySet ? "••••" : string.Empty);

        var embeddingKey = tenant.Settings.EmbeddingApiKey;
        var embeddingApiKeySet = !string.IsNullOrEmpty(embeddingKey);
        var embeddingApiKeyMasked = embeddingApiKeySet && embeddingKey.Length > 4
            ? "••••••••" + embeddingKey[^4..]
            : (embeddingApiKeySet ? "••••" : string.Empty);

        return Ok(this.ApiOk(new TenantAiSettingsResponse(
            provider, model, embeddingProvider, embeddingModel,
            apiKeySet, apiKeyMasked, embeddingApiKeySet, embeddingApiKeyMasked,
            tenant.Settings.RetrievalMode.ToString())));
    }

    [HttpPatch("settings/ai-model")]
    public async Task<ActionResult<ApiResponse>> UpdateAiSettings(
        [FromBody] UpdateAiSettingsRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
            return NotFound(this.ApiFail("Tenant not found."));

        if (!string.IsNullOrEmpty(request.Provider))
            tenant.Settings.LLMProvider = request.Provider.ToLowerInvariant();

        if (!string.IsNullOrEmpty(request.Model))
            tenant.Settings.LLMModel = request.Model;

        if (!string.IsNullOrEmpty(request.EmbeddingProvider))
            tenant.Settings.EmbeddingProvider = request.EmbeddingProvider.ToLowerInvariant();

        if (!string.IsNullOrEmpty(request.EmbeddingModel))
            tenant.Settings.EmbeddingModel = request.EmbeddingModel;

        if (request.ApiKey is not null)
            tenant.Settings.LLMApiKey = request.ApiKey;

        if (request.EmbeddingApiKey is not null)
            tenant.Settings.EmbeddingApiKey = request.EmbeddingApiKey;

        if (!string.IsNullOrEmpty(request.RetrievalMode) &&
            Enum.TryParse<RetrievalMode>(request.RetrievalMode, ignoreCase: true, out var mode))
            tenant.Settings.RetrievalMode = mode;

        var result = await tenantRepository.UpdateAsync(tenant, ct);
        if (!result.Success)
            return StatusCode(500, this.ApiFail(result.Error!));

        // Invalidate cached tenant so query pipeline picks up new settings immediately
        await cache.RemoveAsync(CacheKeys.TenantSettings(tenantId), ct);

        // Update runtime config so LLM/Embedding services use new values immediately
        var activeProvider          = NullIfEmpty(tenant.Settings.LLMProvider)          ?? runtimeConfig.Provider;
        var activeModel             = NullIfEmpty(tenant.Settings.LLMModel)             ?? runtimeConfig.Model;
        var activeEmbeddingProvider = NullIfEmpty(tenant.Settings.EmbeddingProvider)    ?? runtimeConfig.EmbeddingProvider;
        var activeEmbeddingModel    = NullIfEmpty(tenant.Settings.EmbeddingModel)       ?? runtimeConfig.EmbeddingModel;
        var activeKey               = NullIfEmpty(tenant.Settings.LLMApiKey)            ?? runtimeConfig.ApiKey;
        var activeEmbeddingKey      = NullIfEmpty(tenant.Settings.EmbeddingApiKey)      ?? runtimeConfig.EmbeddingApiKey;

        runtimeConfig.Update(activeProvider, activeKey, activeModel, activeEmbeddingModel, activeEmbeddingProvider, activeEmbeddingKey);

        return Ok(new ApiResponse(true, null, this.GetTraceId()));
    }

    [HttpGet("settings/system-prompt")]
    public async Task<ActionResult<ApiResponse<SystemPromptResponse>>> GetSystemPrompt(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
            return NotFound(this.ApiFail("Tenant not found."));

        return Ok(this.ApiOk(new SystemPromptResponse(
            tenant.Settings.SystemPrompt ?? string.Empty,
            PromptDefaults.SystemPrompt)));
    }

    [HttpPatch("settings/system-prompt")]
    public async Task<ActionResult<ApiResponse>> UpdateSystemPrompt(
        [FromBody] UpdateSystemPromptRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
            return NotFound(this.ApiFail("Tenant not found."));

        // An empty value resets the tenant back to the built-in default prompt.
        tenant.Settings.SystemPrompt = (request.SystemPrompt ?? string.Empty).Trim();

        var result = await tenantRepository.UpdateAsync(tenant, ct);
        if (!result.Success)
            return StatusCode(500, this.ApiFail(result.Error!));

        // Invalidate cached tenant so subsequent queries use the new prompt immediately
        await cache.RemoveAsync(CacheKeys.TenantSettings(tenantId), ct);

        return Ok(new ApiResponse(true, null, this.GetTraceId()));
    }

    [HttpGet("settings/provider-models")]
    public async Task<ActionResult<ApiResponse<IEnumerable<string>>>> GetProviderModels(
        [FromQuery] string? provider,
        [FromQuery] string? type,   // "chat" (default) or "embedding"
        CancellationToken ct = default)
    {
        var tenantId = User.GetTenantId();
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
            return NotFound(this.ApiFail("Tenant not found."));

        var effectiveProvider = (provider ?? tenant.Settings.LLMProvider ?? "openai").ToLowerInvariant();
        var apiKey = tenant.Settings.LLMApiKey;
        var isEmbedding = string.Equals(type, "embedding", StringComparison.OrdinalIgnoreCase);

        IEnumerable<string> models = (effectiveProvider, isEmbedding) switch
        {
            ("openai",  false) => await FetchOpenAiModelsAsync(apiKey, ct),
            ("openai",  true)  => await FetchOpenAiEmbeddingModelsAsync(apiKey, ct),
            ("gemini",  false) => await FetchGeminiModelsAsync(apiKey, ct),
            ("gemini",  true)  => await FetchGeminiEmbeddingModelsAsync(apiKey, ct),
            ("groq",    false) => await FetchGroqModelsAsync(apiKey, ct),
            ("claude",  false) => await FetchClaudeModelsAsync(apiKey, ct),
            ("ollama",  _)     => await FetchOllamaModelsInternalAsync(ct),
            _                  => []
        };

        return Ok(this.ApiOk(models));
    }

    [HttpGet("settings/ollama-models")]
    public async Task<ActionResult<ApiResponse<IEnumerable<OllamaModelResponse>>>> GetOllamaModels(CancellationToken ct)
    {
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://ollama:11434";
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        try
        {
            var response = await client.GetAsync($"{baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, this.ApiFail("Ollama API unavailable."));

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement
                .GetProperty("models")
                .EnumerateArray()
                .Select(m => new OllamaModelResponse(
                    m.GetProperty("name").GetString() ?? "",
                    m.TryGetProperty("size", out var s) ? s.GetInt64() : 0))
                .ToList();

            return Ok(this.ApiOk<IEnumerable<OllamaModelResponse>>(models));
        }
        catch (Exception)
        {
            return StatusCode(502, this.ApiFail("Ollama API unavailable."));
        }
    }

    // ── Provider model list helpers ──────────────────────────────────────────

    private async Task<IEnumerable<string>> FetchOpenAiModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) return [];
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var response = await client.GetAsync("https://api.openai.com/v1/models", ct);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id) && IsOpenAiChatModel(id))
                .Order()
                .ToList();
        }
        catch { return []; }
    }

    private async Task<IEnumerable<string>> FetchOpenAiEmbeddingModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) return [];
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var response = await client.GetAsync("https://api.openai.com/v1/models", ct);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => id.StartsWith("text-embedding-", StringComparison.OrdinalIgnoreCase))
                .Order()
                .ToList();
        }
        catch { return []; }
    }

    private async Task<IEnumerable<string>> FetchGeminiEmbeddingModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) return [];
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}", ct);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("models")
                .EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                            methods.EnumerateArray().Any(x => x.GetString() == "embedContent"))
                .Select(m => (m.GetProperty("name").GetString() ?? "").Replace("models/", ""))
                .Where(id => !string.IsNullOrEmpty(id))
                .Order()
                .ToList();
        }
        catch { return []; }
    }

    private static bool IsOpenAiChatModel(string id) =>
        id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("chatgpt-", StringComparison.OrdinalIgnoreCase);

    private async Task<IEnumerable<string>> FetchGeminiModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) return [];
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}", ct);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("models")
                .EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                            methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                .Select(m => (m.GetProperty("name").GetString() ?? "").Replace("models/", ""))
                .Where(id => !string.IsNullOrEmpty(id))
                .Order()
                .ToList();
        }
        catch { return []; }
    }

    private async Task<IEnumerable<string>> FetchGroqModelsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) return [];
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var response = await client.GetAsync("https://api.groq.com/openai/v1/models", ct);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .Order()
                .ToList();
        }
        catch { return []; }
    }

    private async Task<IEnumerable<string>> FetchClaudeModelsAsync(string apiKey, CancellationToken ct)
    {
        IEnumerable<string> fallback = ["claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"];
        if (string.IsNullOrEmpty(apiKey)) return fallback;
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            var response = await client.GetAsync("https://api.anthropic.com/v1/models", ct);
            if (!response.IsSuccessStatusCode) return fallback;
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement
                .GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .Order()
                .ToList();
            return models.Count > 0 ? models : fallback;
        }
        catch { return fallback; }
    }

    private async Task<IEnumerable<string>> FetchOllamaModelsInternalAsync(CancellationToken ct)
    {
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://ollama:11434";
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
        }
        catch { return []; }
    }

    private static string NormalizeProvider(string raw) => raw.ToLowerInvariant() switch
    {
        "openai"      => "OpenAI",
        "azureopenai" => "AzureOpenAI",
        "gemini"      => "Gemini",
        "ollama"      => "Ollama",
        "groq"        => "Groq",
        "claude"      => "Claude",
        _             => raw
    };

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;
}
