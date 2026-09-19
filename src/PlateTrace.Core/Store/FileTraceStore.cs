using System.Text.Json;
using PlateTrace.Core.Models;

namespace PlateTrace.Core.Store;

/// <summary>
/// File-backed store: one JSONL journal per append-only stream plus an atomically
/// rewritten <c>jobs.json</c>. Every mutation serializes the whole small file and
/// publishes it via temp-file + <see cref="File.Replace"/>, so a crash during a
/// commit leaves either the previous file or the complete new one - a batch is
/// never half applied.
/// </summary>
public sealed class FileTraceStore : ITraceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string DataDir { get; }

    public FileTraceStore(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DataDir);
        var formatPath = Path.Combine(DataDir, StoreFormat.FormatFile);
        if (!File.Exists(formatPath))
        {
            await AtomicWriteAsync(formatPath, JsonSerializer.Serialize(new
            {
                format = StoreFormat.FormatName,
                major = StoreFormat.Major,
                minor = StoreFormat.Minor,
                version = StoreFormat.Version,
            }, PlateTraceJson.Options), ct).ConfigureAwait(false);
        }
        else
        {
            await using var stream = File.OpenRead(formatPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("major", out var majorEl) && majorEl.GetInt32() > StoreFormat.Major)
                throw new InvalidDataException(
                    $"{formatPath} was written by store format major version {majorEl.GetInt32()}; this software supports {StoreFormat.Major}.x");
        }

        foreach (var f in new[] { StoreFormat.BatchesFile, StoreFormat.HypothesesFile, StoreFormat.ConclusionsFile })
            if (!File.Exists(Path.Combine(DataDir, f))) File.WriteAllText(Path.Combine(DataDir, f), "");
    }

    public async Task<IReadOnlyList<BatchRecord>> ListBatchesAsync(CancellationToken ct = default) =>
        await ReadJsonlAsync<BatchRecord>(StoreFormat.BatchesFile, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<HypothesisRecord>> ListHypothesesAsync(CancellationToken ct = default) =>
        await ReadJsonlAsync<HypothesisRecord>(StoreFormat.HypothesesFile, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ConclusionRecord>> ListConclusionsAsync(CancellationToken ct = default) =>
        await ReadJsonlAsync<ConclusionRecord>(StoreFormat.ConclusionsFile, ct).ConfigureAwait(false);

    public async Task<BatchRecord?> FindIdempotentAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        var batches = await ListBatchesAsync(ct).ConfigureAwait(false);
        return batches.FirstOrDefault(b => string.Equals(b.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
    }

    public async Task AppendBatchAsync(BatchRecord batch, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(DataDir, StoreFormat.BatchesFile);
            var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false) : Array.Empty<string>();
            var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var json = JsonSerializer.Serialize(batch, PlateTraceJson.Options);
            if (list.Any(l => BatchIdOf(l) == batch.BatchId)) return; // already persisted
            list.Add(json);
            await AtomicWriteAsync(path, string.Join('\n', list) + "\n", ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task AppendHypothesisAsync(HypothesisRecord hypothesis, CancellationToken ct = default)
        => await AppendToJsonlAsync(StoreFormat.HypothesesFile, hypothesis,
            h => $"{h.HypothesisId}#v{h.Version}", h => $"{h.HypothesisId}#v{h.Version}", ct).ConfigureAwait(false);

    public async Task AppendConclusionAsync(ConclusionRecord conclusion, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadJsonlAsync<ConclusionRecord>(StoreFormat.ConclusionsFile, ct).ConfigureAwait(false);
            var list = records.ToList();
            list.RemoveAll(c => c.ConclusionId == conclusion.ConclusionId && c.CreatedAt == conclusion.CreatedAt
                && c.State == conclusion.State);
            list.Add(conclusion);
            await AtomicWriteAsync(Path.Combine(DataDir, StoreFormat.ConclusionsFile),
                string.Join('\n', list.Select(c => JsonSerializer.Serialize(c, PlateTraceJson.Options))) + "\n", ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<JobRecord>> ListJobsAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(DataDir, StoreFormat.JobsFile);
        if (!File.Exists(path)) return Array.Empty<JobRecord>();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<JobRecord>>(stream, PlateTraceJson.Options, ct).ConfigureAwait(false)
               ?? new List<JobRecord>();
    }

    public async Task<JobRecord?> FindJobByIdempotencyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var jobs = await ListJobsAsync(ct).ConfigureAwait(false);
        return jobs.FirstOrDefault(j => string.Equals(j.IdempotencyKey, key, StringComparison.Ordinal));
    }

    public async Task UpsertJobAsync(JobRecord job, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = (await ListJobsAsync(ct).ConfigureAwait(false)).ToList();
            var idx = jobs.FindIndex(j => j.JobId == job.JobId);
            if (idx >= 0) jobs[idx] = job; else jobs.Add(job);
            await AtomicWriteAsync(Path.Combine(DataDir, StoreFormat.JobsFile),
                JsonSerializer.Serialize(jobs, PlateTraceJson.Options), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task AppendToJsonlAsync<T>(string file, T item, Func<T, string> key, Func<T, string> currentKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = (await ReadJsonlAsync<T>(file, ct).ConfigureAwait(false)).ToList();
            var json = JsonSerializer.Serialize(item, PlateTraceJson.Options);
            var recordsJson = records.Select(r => JsonSerializer.Serialize(r, PlateTraceJson.Options)).ToList();
            var k = currentKey(item);
            var existing = recordsJson.FirstOrDefault(l =>
            {
                using var doc = JsonDocument.Parse(l);
                var id = doc.RootElement.TryGetProperty("hypothesisId", out var h) ? h.GetString() : null;
                var ver = doc.RootElement.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
                return $"{id}#v{ver}" == k;
            });
            if (existing is not null) recordsJson.Remove(existing);
            recordsJson.Add(json);
            await AtomicWriteAsync(Path.Combine(DataDir, file), string.Join('\n', recordsJson.Where(s => s.Length > 0)) + "\n", ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<T>> ReadJsonlAsync<T>(string file, CancellationToken ct)
    {
        var path = Path.Combine(DataDir, file);
        var result = new List<T>();
        if (!File.Exists(path)) return result;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        string? line;
        var lineNo = 0;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            T? item;
            try { item = JsonSerializer.Deserialize<T>(line, PlateTraceJson.Options); }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"corrupt record in {file} line {lineNo}: {ex.Message}", ex);
            }
            if (item is not null) result.Add(item);
        }
        return result;
    }

    private static string? BatchIdOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("batchId", out var b) ? b.GetString() : null;
        }
        catch { return null; }
    }

    private async Task AtomicWriteAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await using (var crossLock = new FileStream(Path.Combine(DataDir, StoreFormat.LockFile),
                         FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(fs);
            await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            fs.Flush(true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
