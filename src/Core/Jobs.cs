using System.Text.Json;

namespace PairwiseGsb.Core;

public enum JobStatus { Pending, Running, Done }

public sealed class JobRecord
{
    public required string JobId { get; init; }
    public required string Kind { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Durable background jobs. A crash mid-job leaves the record Running; on
/// recovery it is re-executed, but the result is written at most once per
/// job id, so recovery never double-records results.
/// </summary>
public sealed class JobRunner
{
    private readonly string _path;
    private readonly List<JobRecord> _jobs;

    public JobRunner(string dataDir)
    {
        _path = Path.Combine(dataDir, "jobs.json");
        _jobs = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(_path), RuleSet.Json) ?? new List<JobRecord>()
            : new List<JobRecord>();
        // Recovery: anything not Done becomes eligible to run again.
        foreach (var j in _jobs.Where(j => j.Status != JobStatus.Done))
            j.Status = JobStatus.Pending;
        Save();
    }

    public IReadOnlyList<JobRecord> Jobs => _jobs;

    public JobRecord Enqueue(string jobId, string kind)
    {
        var existing = _jobs.FirstOrDefault(j => j.JobId == jobId);
        if (existing is not null) return existing; // idempotent enqueue
        var job = new JobRecord { JobId = jobId, Kind = kind };
        _jobs.Add(job);
        Save();
        return job;
    }

    public int RunPending(Func<JobRecord, string> execute)
    {
        var ran = 0;
        foreach (var job in _jobs.Where(j => j.Status == JobStatus.Pending).ToList())
        {
            job.Status = JobStatus.Running;
            Save();
            var result = execute(job);
            // Write-once: a result that already exists is never overwritten
            // by a duplicate execution.
            if (job.ResultJson is null)
            {
                job.ResultJson = result;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            job.Status = JobStatus.Done;
            Save();
            ran++;
        }
        return ran;
    }

    private void Save() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(_jobs, RuleSet.Json));
}
