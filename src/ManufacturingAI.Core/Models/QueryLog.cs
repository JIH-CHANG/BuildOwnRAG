namespace ManufacturingAI.Core.Models;

public class QueryLog
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public List<string> SourceChunkIds { get; set; } = [];
    public double ConfidenceScore { get; set; }
    public QueryFeedback? Feedback { get; set; }
    public long LatencyMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum QueryFeedback { Positive, Negative }
