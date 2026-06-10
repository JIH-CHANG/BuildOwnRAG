namespace ManufacturingAI.Core.Common;

// Thrown when a request needs the tenant's vector collection while it is being
// migrated to a new embedding dimension (e.g. after the embedding model changed).
// GlobalExceptionMiddleware maps this to 503 Service Unavailable.
public class MigrationInProgressException(Guid tenantId) : Exception(
    "Index migration in progress — the document index is being rebuilt for a new embedding model. Please try again in a few minutes.")
{
    public Guid TenantId { get; } = tenantId;
}
