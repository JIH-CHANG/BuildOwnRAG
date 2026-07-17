using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ManufacturingAI.Core.Configuration;
using ManufacturingAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.Core.RAG.Memory;

public class QaMemoryEntry
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public int Positive { get; set; }
    public int Negative { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int Votes => Positive + Negative;
    public double Accuracy => Votes == 0 ? 0.0 : (double)Positive / Votes;
}

// Stores QA memory as one human-readable markdown file per tenant
// ({Folder}/tenant_{tenantId}.md). Each entry is a "## Q:" section whose vote
// counts live in an HTML comment so the file stays hand-editable while parsing
// stays unambiguous. All operations serialize per tenant via a semaphore; all
// public methods swallow errors (memory must never fail a query).
public partial class MarkdownQaMemoryService(
    QaMemoryOptions options,
    ILogger<MarkdownQaMemoryService> logger) : IQaMemoryService
{
    private const string FileHeader =
        """
        # QA Memory

        > Maintained automatically. Every answered question is recorded here; user
        > thumbs up/down feedback updates each entry's accuracy. At query time,
        > validated entries (accuracy above threshold) are injected into the prompt
        > as trusted reference and rejected ones as mistakes to avoid, so answers
        > improve over time.

        """;

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    [GeneratedRegex(@"^<!--\s*qa-meta\s+positive=(\d+)\s+negative=(\d+)\s+updated=(\S+)\s*-->\s*$")]
    private static partial Regex MetaRegex();

    public async Task<string?> BuildMemoryContextAsync(Guid tenantId, string question, CancellationToken ct = default)
    {
        if (!options.Enabled) return null;
        try
        {
            List<QaMemoryEntry> entries;
            var gate = GetLock(tenantId);
            await gate.WaitAsync(ct);
            try { entries = await LoadAsync(tenantId, ct); }
            finally { gate.Release(); }

            if (entries.Count == 0) return null;

            var queryTokens = Tokenize(question);
            if (queryTokens.Count == 0) return null;

            var scored = entries
                .Select(e => (Entry: e, Similarity: Similarity(queryTokens, Tokenize(e.Question))))
                .Where(x => x.Similarity >= options.MinSimilarity)
                .OrderByDescending(x => x.Similarity)
                .ToList();

            var validated = scored
                .Where(x => IsTrusted(x.Entry))
                .Take(options.MaxInjectedValidated)
                .Select(x => x.Entry)
                .ToList();
            var rejected = scored
                .Where(x => IsRejected(x.Entry))
                .Take(options.MaxInjectedRejected)
                .Select(x => x.Entry)
                .ToList();

            if (validated.Count == 0 && rejected.Count == 0) return null;

            var sb = new StringBuilder();
            if (validated.Count > 0)
            {
                sb.AppendLine("Previously validated answers (confirmed correct by users; prefer their content when the current question matches):");
                foreach (var e in validated)
                {
                    sb.AppendLine($"Q: {e.Question}");
                    sb.AppendLine($"A: {e.Answer}");
                    sb.AppendLine();
                }
            }
            if (rejected.Count > 0)
            {
                sb.AppendLine("Previous answers marked INCORRECT by users (do not repeat these mistakes; rely on the reference documents instead):");
                foreach (var e in rejected)
                {
                    sb.AppendLine($"Q: {e.Question}");
                    sb.AppendLine($"Incorrect answer (excerpt): {Excerpt(e.Answer, 300)}");
                    sb.AppendLine();
                }
            }
            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QA memory: failed to build context for tenant {TenantId}.", tenantId);
            return null;
        }
    }

    public async Task RecordAnswerAsync(Guid tenantId, string question, string answer, CancellationToken ct = default)
    {
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) return;
        try
        {
            var gate = GetLock(tenantId);
            await gate.WaitAsync(ct);
            try
            {
                var entries = await LoadAsync(tenantId, ct);
                var key = NormalizeKey(question);
                var existing = entries.FirstOrDefault(e => NormalizeKey(e.Question) == key);

                if (existing is null)
                {
                    entries.Add(new QaMemoryEntry
                    {
                        Question = OneLine(question),
                        Answer = answer.Trim(),
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else if (!IsTrusted(existing))
                {
                    // A new attempt replaces an unverified/rejected answer and gets a
                    // fresh evaluation; a trusted (user-confirmed) answer is kept as-is.
                    existing.Answer = answer.Trim();
                    existing.Positive = 0;
                    existing.Negative = 0;
                    existing.UpdatedAt = DateTime.UtcNow;
                }

                Prune(entries);
                await SaveAsync(tenantId, entries, ct);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QA memory: failed to record answer for tenant {TenantId}.", tenantId);
        }
    }

    public async Task ApplyFeedbackAsync(Guid tenantId, string question, string answer, QueryFeedback feedback, CancellationToken ct = default)
    {
        if (!options.Enabled) return;
        try
        {
            var gate = GetLock(tenantId);
            await gate.WaitAsync(ct);
            try
            {
                var entries = await LoadAsync(tenantId, ct);
                var key = NormalizeKey(question);
                var entry = entries.FirstOrDefault(e => NormalizeKey(e.Question) == key);

                if (entry is null)
                {
                    // Feedback for an answer the file no longer has (e.g. pruned or
                    // deleted file): recreate the entry so the signal is not lost.
                    entry = new QaMemoryEntry { Question = OneLine(question), Answer = answer.Trim() };
                    entries.Add(entry);
                }
                else if (entry.Answer != answer.Trim())
                {
                    // The user rated a different generation than the one stored. Only
                    // adopt it when the stored answer has no votes of its own;
                    // otherwise the vote would corrupt an already-rated answer.
                    if (entry.Votes > 0) return;
                    entry.Answer = answer.Trim();
                }

                if (feedback == QueryFeedback.Positive) entry.Positive++;
                else entry.Negative++;
                entry.UpdatedAt = DateTime.UtcNow;

                Prune(entries);
                await SaveAsync(tenantId, entries, ct);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QA memory: failed to apply feedback for tenant {TenantId}.", tenantId);
        }
    }

    private bool IsTrusted(QaMemoryEntry e) => e.Positive > 0 && e.Accuracy >= options.MinAccuracy;
    private bool IsRejected(QaMemoryEntry e) => e.Negative > 0 && e.Accuracy < options.MinAccuracy;

    private void Prune(List<QaMemoryEntry> entries)
    {
        while (entries.Count > options.MaxEntries)
        {
            var victim = entries
                .OrderBy(IsTrusted) // unverified/rejected go first
                .ThenBy(e => e.UpdatedAt)
                .First();
            entries.Remove(victim);
        }
    }

    // ---- markdown file I/O ----

    private string FilePath(Guid tenantId)
    {
        var folder = string.IsNullOrWhiteSpace(options.Folder)
            ? Path.Combine(AppContext.BaseDirectory, "memory")
            : options.Folder;
        return Path.Combine(folder, $"tenant_{tenantId}.md");
    }

    private async Task<List<QaMemoryEntry>> LoadAsync(Guid tenantId, CancellationToken ct)
    {
        var path = FilePath(tenantId);
        if (!File.Exists(path)) return [];

        var entries = new List<QaMemoryEntry>();
        QaMemoryEntry? current = null;
        var answer = new StringBuilder();

        void Flush()
        {
            if (current is null) return;
            current.Answer = Unescape(answer.ToString().Trim());
            if (current.Question.Length > 0 && current.Answer.Length > 0)
                entries.Add(current);
            current = null;
            answer.Clear();
        }

        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (line.StartsWith("## Q: ", StringComparison.Ordinal))
            {
                Flush();
                current = new QaMemoryEntry { Question = line["## Q: ".Length..].Trim() };
            }
            else if (current is not null && MetaRegex().Match(line) is { Success: true } m)
            {
                current.Positive = int.Parse(m.Groups[1].Value);
                current.Negative = int.Parse(m.Groups[2].Value);
                if (DateTime.TryParse(m.Groups[3].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var updated))
                    current.UpdatedAt = updated;
            }
            else if (current is not null)
            {
                answer.AppendLine(line);
            }
        }
        Flush();
        return entries;
    }

    private async Task SaveAsync(Guid tenantId, List<QaMemoryEntry> entries, CancellationToken ct)
    {
        var path = FilePath(tenantId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sb = new StringBuilder(FileHeader);
        foreach (var e in entries.OrderByDescending(e => e.UpdatedAt))
        {
            sb.AppendLine();
            sb.AppendLine($"## Q: {OneLine(e.Question)}");
            sb.AppendLine($"<!-- qa-meta positive={e.Positive} negative={e.Negative} updated={e.UpdatedAt:yyyy-MM-ddTHH:mm:ssZ} -->");
            sb.AppendLine();
            sb.AppendLine(Escape(e.Answer));
        }

        // Write-then-move so a crash mid-write cannot truncate the memory file.
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, sb.ToString(), ct);
        File.Move(tmp, path, overwrite: true);
    }

    // An answer line that itself starts with "## Q: " would be parsed as a new
    // entry; escape it on write and undo on read.
    private static string Escape(string answer)
        => string.Join('\n', answer.Split('\n')
            .Select(l => l.StartsWith("## Q: ", StringComparison.Ordinal) ? "\\" + l : l));

    private static string Unescape(string answer)
        => string.Join('\n', answer.Split('\n')
            .Select(l => l.StartsWith("\\## Q: ", StringComparison.Ordinal) ? l[1..] : l));

    private static string OneLine(string s)
        => Regex.Replace(s.Trim(), @"\s+", " ");

    private static string NormalizeKey(string question)
        => OneLine(question).ToLowerInvariant();

    private static string Excerpt(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static SemaphoreSlim GetLock(Guid tenantId)
        => Locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));

    // ---- question similarity (same CJK-aware scheme as the Lite keyword
    // prefilter: Latin whole words, CJK overlapping bigrams) ----

    private static readonly char[] Separators =
        [' ', '\t', '\n', '\r', ',', '.', '，', '。', '、', '；', ';', '?', '？', '!', '！', ':', '：'];

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;

            if (ContainsCjk(s))
            {
                if (s.Length == 1) tokens.Add(s);
                else
                    for (int i = 0; i < s.Length - 1; i++)
                        tokens.Add(s.Substring(i, 2));
            }
            else if (s.Length >= 2)
            {
                tokens.Add(s);
            }
        }
        return tokens;
    }

    // Fraction of the query's tokens covered by the entry's question.
    private static double Similarity(HashSet<string> queryTokens, HashSet<string> entryTokens)
        => queryTokens.Count == 0 ? 0.0 : (double)queryTokens.Count(entryTokens.Contains) / queryTokens.Count;

    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
            if ((ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3400 && ch <= 0x4DBF)
                || (ch >= 0xF900 && ch <= 0xFAFF))
                return true;
        return false;
    }
}
