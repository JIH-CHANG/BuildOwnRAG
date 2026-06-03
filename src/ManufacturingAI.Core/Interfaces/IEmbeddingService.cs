namespace ManufacturingAI.Core.Interfaces;

public interface IEmbeddingService
{
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IEnumerable<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
}
