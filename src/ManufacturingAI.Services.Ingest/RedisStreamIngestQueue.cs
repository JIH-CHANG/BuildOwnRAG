using StackExchange.Redis;
using System.Text.Json;

namespace ManufacturingAI.Services.Ingest;

internal sealed class RedisStreamIngestQueue(IConnectionMultiplexer redis) : IIngestQueue
{
    internal const string StreamKey = "docs:ingest";
    internal const string GroupName = "ingest-workers";
    private const int MaxStreamLength = 10_000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IDatabase _db = redis.GetDatabase();

    // ── Producer ─────────────────────────────────────────────────

    public async Task EnqueueAsync(IngestJobMessage message, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(message);
        await _db.StreamAddAsync(
            StreamKey,
            [new NameValueEntry("payload", payload)],
            maxLength: MaxStreamLength,
            useApproximateMaxLength: true);
    }

    // ── Consumer ─────────────────────────────────────────────────

    public async Task<(IngestJobMessage Message, string EntryId)?> DequeueAsync(
        string consumerName, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            StreamEntry[] entries;
            try
            {
                entries = await _db.StreamReadGroupAsync(
                    StreamKey, GroupName, consumerName,
                    position: ">",
                    count: 1);
            }
            catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP"))
            {
                // Consumer group not yet initialised — back off and retry
                await Delay(ct);
                continue;
            }

            if (entries.Length > 0)
            {
                var entry = entries[0];
                var payload = (string?)entry["payload"]
                    ?? throw new InvalidOperationException(
                        $"Stream entry {entry.Id} has no 'payload' field.");
                var message = JsonSerializer.Deserialize<IngestJobMessage>(payload)
                    ?? throw new InvalidOperationException(
                        $"Failed to deserialize IngestJobMessage from entry {entry.Id}.");
                return (message, entry.Id.ToString());
            }

            await Delay(ct);
        }

        return null;
    }

    public async Task AckAsync(string entryId, CancellationToken ct = default)
        => await _db.StreamAcknowledgeAsync(StreamKey, GroupName, entryId);

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task Delay(CancellationToken ct)
    {
        try { await Task.Delay(PollInterval, ct); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }
}
