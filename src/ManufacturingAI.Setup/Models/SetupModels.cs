namespace ManufacturingAI.Setup.Models;

public record TestConnectionsRequest(
    string PostgresHost,
    int PostgresPort,
    string PostgresDb,
    string PostgresUser,
    string PostgresPassword,
    string RedisHost,
    int RedisPort,
    string QdrantHost,
    int QdrantHttpPort
);

public record ConnectionResult(string Service, bool Ok, string Hint);

public record TestConnectionsResponse(List<ConnectionResult> Results);

public record TestLlmRequest(
    string Provider,
    string ApiKey,
    string Model,
    string? AzureEndpoint = null,
    string? OllamaBaseUrl = null
);

public record LlmTestResult(bool Ok, string Error);

public record InstallRequest(
    string PostgresHost,
    int PostgresPort,
    string PostgresDb,
    string PostgresUser,
    string PostgresPassword,
    string RedisHost,
    int RedisPort,
    string QdrantHost,
    int QdrantHttpPort,
    string LlmProvider,
    string LlmApiKey,
    string LlmChatModel,
    string? AzureEndpoint,
    string? AzureDeployment,
    string? OllamaBaseUrl,
    string? OllamaChatModel,
    string? GeminiApiKey,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions,
    string? OllamaEmbeddingModel,
    string? GeminiEmbeddingModel,
    string? EmbeddingApiKey,
    string OrgName,
    string AdminEmail,
    string AdminPassword
);

public record InstallProgress(string Step, string Status, string Message);

public record SetupStatusResponse(bool IsCompleted, int Step);
