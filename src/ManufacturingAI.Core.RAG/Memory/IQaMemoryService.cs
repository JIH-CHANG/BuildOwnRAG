using ManufacturingAI.Core.Models;

namespace ManufacturingAI.Core.RAG.Memory;

// Feedback-driven QA memory. Every answer is recorded into a per-tenant
// markdown file; user feedback (thumbs up/down on a QueryLog) updates each
// entry's accuracy; at query time, entries similar to the incoming question
// are injected into the prompt — validated ones as trusted reference,
// rejected ones as mistakes to avoid. Implementations must never throw:
// memory is an enhancement and must not break the query pipeline.
public interface IQaMemoryService
{
    // Prompt block with validated/rejected past answers similar to the
    // question, or null when there is nothing relevant (or memory is disabled).
    Task<string?> BuildMemoryContextAsync(Guid tenantId, string question, CancellationToken ct = default);

    // Record a freshly generated answer. Trusted (user-confirmed) entries keep
    // their validated answer; unverified/rejected ones are replaced by the new
    // attempt and their vote counts reset so it gets a fresh evaluation.
    Task RecordAnswerAsync(Guid tenantId, string question, string answer, CancellationToken ct = default);

    // Apply thumbs feedback to the entry for this question. The answer the user
    // rated is passed so feedback is not counted against a newer, different answer.
    Task ApplyFeedbackAsync(Guid tenantId, string question, string answer, QueryFeedback feedback, CancellationToken ct = default);
}
