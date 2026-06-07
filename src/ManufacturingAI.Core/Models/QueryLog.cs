using System.Text.Json.Serialization;

namespace ManufacturingAI.Core.Models;

public class QueryLog
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public List<string> SourceChunkIds { get; set; } = [];

    // Per-query retrieval detail: the reranked chunks with their scores and a
    // content excerpt, captured for the Analytics query inspector. Serialized as
    // JSON. Hybrid mode fills all score fields; Lite mode records rank + content
    // only (BM25-only retrieval, no vector/fusion scores).
    public List<RetrievedChunkLog> RetrievedChunks { get; set; } = [];

    public double ConfidenceScore { get; set; }
    public QueryFeedback? Feedback { get; set; }
    public long LatencyMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

// One retrieved chunk as recorded against a QueryLog. Scores are nullable so
// Lite mode (which has no vector/fusion scores) can omit them.
public class RetrievedChunkLog
{
    public string ChunkId { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string SourceTitle { get; set; } = string.Empty;
    public string ContentExcerpt { get; set; } = string.Empty;
    public float? VectorScore { get; set; }
    public float? BM25Score { get; set; }
    public float? FusionScore { get; set; }
}

// Serialized as its name ("Positive"/"Negative") so the feedback API binds the
// string the frontend sends, rather than the default numeric enum value.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryFeedback { Positive, Negative }
