namespace ManufacturingAI.Infrastructure.LLM;

public sealed class LlmRuntimeConfig
{
    private volatile string _provider = string.Empty;
    private volatile string _apiKey = string.Empty;
    private volatile string _model = string.Empty;
    private volatile string _embeddingProvider = string.Empty;
    private volatile string _embeddingApiKey = string.Empty;
    private volatile string _embeddingModel = string.Empty;
    private int _version = 0;

    public string Provider => _provider;
    public string ApiKey => _apiKey;
    public string Model => _model;
    /// <summary>Embedding provider — may differ from LLM Provider (e.g. Groq LLM + OpenAI embeddings).</summary>
    public string EmbeddingProvider => _embeddingProvider;
    /// <summary>API key for the embedding provider — only needed when different from the LLM provider.</summary>
    public string EmbeddingApiKey => _embeddingApiKey;
    public string EmbeddingModel => _embeddingModel;
    public int Version => _version;

    public void Update(string provider, string apiKey, string model,
        string embeddingModel = "", string embeddingProvider = "", string embeddingApiKey = "")
    {
        _provider = provider;
        _apiKey = apiKey;
        _model = model;
        _embeddingProvider = embeddingProvider;
        _embeddingApiKey = embeddingApiKey;
        _embeddingModel = embeddingModel;
        Interlocked.Increment(ref _version);
    }
}
