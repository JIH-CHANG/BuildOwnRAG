using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using Microsoft.ML.Tokenizers;

namespace ManufacturingAI.Core.RAG.Chunking;

public class SemanticChunker : IDocumentChunker
{
    // Uses tiktoken cl100k_base for token counting (GPT-3.5/4 compatible)
    private static readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    public IEnumerable<TextChunk> Chunk(ParsedDocument document, ChunkOptions options)
    {
        var chunks = new List<TextChunk>();
        int chunkIndex = 0;

        if (document.Sections is { Count: > 0 })
        {
            // Preferred: split by parsed sections
            foreach (var section in document.Sections)
            {
                var sectionChunks = SplitSection(
                    section.Content,
                    section.Title,
                    section.PageNumber,
                    document.Metadata,
                    options,
                    ref chunkIndex);

                chunks.AddRange(sectionChunks);
            }
        }
        else
        {
            // Fallback: split entire plain text when no sections are available
            var fallbackChunks = SplitSection(
                document.PlainText,
                title: null,
                pageNumber: null,
                document.Metadata,
                options,
                ref chunkIndex);

            chunks.AddRange(fallbackChunks);
        }

        return chunks;
    }

    private static IEnumerable<TextChunk> SplitSection(
        string text,
        string? title,
        int? pageNumber,
        Dictionary<string, string> docMeta,
        ChunkOptions options,
        ref int chunkIndex)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // short content — skip sentence splitting entirely
        if (CountTokens(text) <= options.MaxTokens)
            return [CreateChunk([text.Trim()], title, pageNumber, docMeta, chunkIndex++)];

        var chunks = new List<TextChunk>();
        var sentences = SplitIntoSentences(text);

        var buffer = new List<string>();
        int bufferTokens = 0;

        for (int i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            int sentenceTokens = CountTokens(sentence);

            if (bufferTokens + sentenceTokens > options.MaxTokens && buffer.Count > 0)
            {
                chunks.Add(CreateChunk(buffer, title, pageNumber, docMeta, chunkIndex++));

                // Overlap: carry over the last few sentences into the next chunk
                buffer = TrimOverlap(buffer, options.Overlap);
                bufferTokens = buffer.Sum(CountTokens);
            }

            buffer.Add(sentence);
            bufferTokens += sentenceTokens;
        }

        if (buffer.Count > 0)
            chunks.Add(CreateChunk(buffer, title, pageNumber, docMeta, chunkIndex++));

        return chunks;
    }

    private static TextChunk CreateChunk(
        List<string> sentences,
        string? sectionTitle,
        int? pageNumber,
        Dictionary<string, string> docMeta,
        int index)
    {
        docMeta.TryGetValue("title", out var sourceTitle);
        docMeta.TryGetValue("sourceType", out var sourceType);

        return new TextChunk(
            Content: string.Join(" ", sentences),
            ChunkIndex: index,
            Metadata: new ChunkMetadata
            {
                SourceTitle = sourceTitle ?? string.Empty,
                SectionTitle = sectionTitle ?? string.Empty,
                PageNumber = pageNumber,
                SourceType = sourceType ?? string.Empty,
                DocumentUpdatedAt = DateTime.UtcNow
            });
    }

    private static List<string> TrimOverlap(List<string> sentences, int overlapTokens)
    {
        var result = new List<string>();
        int tokens = 0;

        for (int i = sentences.Count - 1; i >= 0; i--)
        {
            int t = CountTokens(sentences[i]);
            if (tokens + t > overlapTokens) break;
            result.Insert(0, sentences[i]);
            tokens += t;
        }

        return result;
    }

    private static List<string> SplitIntoSentences(string text)
    {
        // Split on sentence-ending punctuation (CJK and ASCII)
        var separators = new[] { "。", "？", "！", ". ", "? ", "! ", "\n\n" };
        var sentences = new List<string>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            foreach (var sep in separators)
            {
                if (i + sep.Length <= text.Length && text.Substring(i, sep.Length) == sep)
                {
                    var sentence = text.Substring(start, i + sep.Length - start).Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                        sentences.Add(sentence);
                    start = i + sep.Length;
                    i = start - 1;
                    break;
                }
            }
        }

        if (start < text.Length)
        {
            var last = text.Substring(start).Trim();
            if (!string.IsNullOrWhiteSpace(last))
                sentences.Add(last);
        }

        return sentences;
    }

    private static int CountTokens(string text)
        => _tokenizer.CountTokens(text);
}
