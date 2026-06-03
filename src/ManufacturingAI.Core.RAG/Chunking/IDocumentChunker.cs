using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;

namespace ManufacturingAI.Core.RAG.Chunking;

public record ChunkOptions(int MaxTokens = 512, int Overlap = 50);
public record TextChunk(string Content, int ChunkIndex, ChunkMetadata Metadata);

public interface IDocumentChunker
{
    IEnumerable<TextChunk> Chunk(ParsedDocument document, ChunkOptions options);
}
