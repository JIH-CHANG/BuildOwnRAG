namespace ManufacturingAI.Core.Models;

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;
    public DateTime LicenseExpiresAt { get; set; }
    public TenantPlan Plan { get; set; }
    public TenantSettings Settings { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    // Tracks the active Qdrant collection version (1 = tenant_{id}, N = tenant_{id}_vN)
    public int CollectionVersion { get; set; } = 1;
}

public class TenantSettings
{
    public int MaxUsers { get; set; }
    public int MaxDocuments { get; set; }
    public int MaxConnectors { get; set; }
    public string LLMProvider { get; set; } = string.Empty;          // openai / azureopenai / gemini / ollama / groq
    public string LLMModel { get; set; } = string.Empty;
    public string LLMApiKey { get; set; } = string.Empty;            // stored encrypted
    public string EmbeddingProvider { get; set; } = string.Empty;    // openai / azureopenai / gemini / ollama (may differ from LLMProvider)
    public string EmbeddingApiKey { get; set; } = string.Empty;      // only needed when EmbeddingProvider ≠ LLMProvider
    public string EmbeddingModel { get; set; } = string.Empty;
    // Recorded at collection creation; migration is triggered when this changes
    public int EmbeddingDimensions { get; set; }
    // Retrieval pipeline: Hybrid (Qdrant + BM25) or Markdown (BM25-only, no embeddings/Qdrant)
    public RetrievalMode RetrievalMode { get; set; } = RetrievalMode.Hybrid;
    // Custom system-prompt instructions; empty = use PromptDefaults.SystemPrompt.
    public string SystemPrompt { get; set; } = string.Empty;
}

public enum TenantPlan { Free, Trial, Starter, Professional, Enterprise }

public enum RetrievalMode { Hybrid, Markdown }
