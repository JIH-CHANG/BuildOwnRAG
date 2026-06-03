namespace ManufacturingAI.Setup;

internal sealed class SetupState
{
    // ── Services ─────────────────────────────────────────────────────────────
    public string PostgresHost { get; set; } = "localhost";
    public int PostgresPort { get; set; } = 5432;
    public string PostgresDb { get; set; } = "manufacturingai";
    public string PostgresUser { get; set; } = "postgres";
    public string PostgresPassword { get; set; } = "";
    public string RedisHost { get; set; } = "localhost";
    public int RedisPort { get; set; } = 6379;
    public string QdrantHost { get; set; } = "localhost";
    public int QdrantHttpPort { get; set; } = 6333;
    public int QdrantGrpcPort => QdrantHttpPort + 1;

    // ── LLM ──────────────────────────────────────────────────────────────────
    public string LlmProvider { get; set; } = "openai";
    public string LlmApiKey { get; set; } = "";
    public string LlmChatModel { get; set; } = "gpt-4o-mini";
    public string AzureEndpoint { get; set; } = "";
    public string AzureDeployment { get; set; } = "";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaChatModel { get; set; } = "qwen2.5";
    public string GeminiApiKey { get; set; } = "";
    public string GeminiChatModel { get; set; } = "gemini-3.1-flash-lite";
    public string GeminiEmbeddingModel { get; set; } = "text-embedding-004";

    // ── Embedding ─────────────────────────────────────────────────────────────
    public string EmbeddingProvider { get; set; } = "openai";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public int EmbeddingDimensions { get; set; } = 1536;
    public string OllamaEmbeddingModel { get; set; } = "nomic-embed-text";
    public string EmbeddingApiKey { get; set; } = "";

    // ── Admin account ─────────────────────────────────────────────────────────
    public string OrgName { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string AdminPassword { get; set; } = "";

    // ── Generated secrets ─────────────────────────────────────────────────────
    public string JwtSecret { get; set; } = "";
    public string EncryptionKey { get; set; } = "";
    public string EncryptionIv { get; set; } = "";

    // ── Helpers ───────────────────────────────────────────────────────────────
    public string PostgresConnectionString =>
        $"Host={PostgresHost};Port={PostgresPort};Database={PostgresDb};Username={PostgresUser};Password={PostgresPassword}";

    public string RedisConnectionString =>
        $"{RedisHost}:{RedisPort}";

    public string QdrantHealthUrl =>
        $"http://{QdrantHost}:{QdrantHttpPort}/healthz";
}
