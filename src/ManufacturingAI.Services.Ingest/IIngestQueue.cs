namespace ManufacturingAI.Services.Ingest;

public interface IIngestQueue
{
    /// <summary>Enqueue a sync job (producer side).</summary>
    Task EnqueueAsync(IngestJobMessage message, CancellationToken ct = default);

    /// <summary>
    /// Block until one message is available and return it with its stream entry ID.
    /// Returns null only when <paramref name="ct"/> is cancelled.
    /// Called by IngestWorker (consumer side).
    /// </summary>
    Task<(IngestJobMessage Message, string EntryId)?> DequeueAsync(
        string consumerName, CancellationToken ct = default);

    /// <summary>
    /// Acknowledge a successfully processed message (XACK).
    /// Called by IngestWorker after successful processing.
    /// </summary>
    Task AckAsync(string entryId, CancellationToken ct = default);
}
