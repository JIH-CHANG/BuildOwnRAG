namespace ManufacturingAI.Core.Configuration;

// Settings for the feedback-driven QA memory (per-tenant markdown file that
// records past answers and their user-feedback accuracy, and is injected back
// into prompts). Bound from the "QaMemory" config section, singleton POCO.
public class QaMemoryOptions
{
    public bool Enabled { get; set; } = true;

    // Folder holding tenant_{tenantId}.md files. Empty = {BaseDirectory}/memory.
    public string Folder { get; set; } = string.Empty;

    public int MaxEntries { get; set; } = 500;      // per-tenant cap; oldest unverified entries pruned first

    public int MaxInjectedValidated { get; set; } = 3; // user-confirmed answers injected as trusted reference
    public int MaxInjectedRejected { get; set; } = 2;  // downvoted answers injected as mistakes to avoid

    // Entry is trusted when Positive > 0 and accuracy >= MinAccuracy;
    // rejected when Negative > 0 and accuracy < MinAccuracy.
    public double MinAccuracy { get; set; } = 0.5;

    // Fraction of the incoming question's tokens an entry's question must cover
    // before the entry is considered "the same question" and injected.
    public double MinSimilarity { get; set; } = 0.55;
}
