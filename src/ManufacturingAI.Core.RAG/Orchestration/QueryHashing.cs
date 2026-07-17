using System.Security.Cryptography;
using System.Text;

namespace ManufacturingAI.Core.RAG.Orchestration;

// Hash used for the Redis query-result cache key. Shared so feedback handling
// can invalidate the exact key the orchestrator wrote.
public static class QueryHashing
{
    public static string Compute(string question)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(question));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
