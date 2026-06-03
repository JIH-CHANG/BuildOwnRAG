namespace ManufacturingAI.Setup.Models;

internal record TestConnectionsRequest(
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

internal record ConnectionResult(string Service, bool Ok, string Hint);

internal record TestConnectionsResponse(List<ConnectionResult> Results);

internal record TestLlmRequest(
    string Provider,
    string ApiKey,
    string Model,
    string? AzureEndpoint = null,
    string? OllamaBaseUrl = null
);

internal record LlmTestResult(bool Ok, string Error);

internal record InstallRequest(
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

internal record InstallProgress(string Step, string Status, string Message);

internal record SetupStatusResponse(bool IsCompleted, int Step);
