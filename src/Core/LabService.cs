namespace PairwiseGsb.Core;

/// <summary>Facade tying the store, projection, hypotheses, conclusion
/// lifecycle and background jobs together. Thread-safe for the web host.</summary>
public sealed class LabService
{
    private readonly object _gate = new();
    private readonly EventStore _store;
    private readonly HypothesisStore _hypotheses;
    private readonly ConclusionStore _conclusions;
    private readonly JobRunner _jobs;
    private readonly string _dataDir;

    public LabService(string dataDir)
    {
        _dataDir = dataDir;
        _store = new EventStore(dataDir);
        _hypotheses = new HypothesisStore(dataDir);
        _conclusions = new ConclusionStore(dataDir);
        _jobs = new JobRunner(dataDir);
        RecoverJobs();
    }

    public EventStore Store => _store;
    public JobRunner Jobs => _jobs;
    public ConclusionStore Conclusions => _conclusions;
    public HypothesisStore Hypotheses => _hypotheses;

    public LabState CurrentState()
    {
        lock (_gate)
            return LabState.Build(_store.EffectiveEvents());
    }

    public BatchCommitResult SubmitBatch(string idempotencyKey, List<LabEvent> events)
    {
        lock (_gate)
        {
            var result = _store.SubmitBatch(idempotencyKey, events);
            if (result.Applied && !result.WasReplay &&
                events.Any(e => e is CorrectionRecorded))
                _conclusions.OnUpstreamCorrection();
            if (result.Applied && !result.WasReplay)
                _jobs.Enqueue($"recompute-{idempotencyKey}", "recompute");
            return result;
        }
    }

    public TraceResult Trace(double readingThreshold, string unit, double minContributionFraction = 0.0)
    {
        lock (_gate)
            return Tracer.Trace(CurrentStateUnsafe(), readingThreshold, unit, minContributionFraction);
    }

    public HypothesisAnalysis AnalyzeHypotheses(double readingThreshold, string unit)
    {
        lock (_gate)
        {
            var state = CurrentStateUnsafe();
            var trace = Tracer.Trace(state, readingThreshold, unit);
            return HypothesisStore.Analyze(state, trace, _hypotheses.Current());
        }
    }

    public Hypothesis UpsertHypothesis(Hypothesis h)
    {
        lock (_gate)
            return _hypotheses.Upsert(h);
    }

    public PublishedConclusion Publish(double readingThreshold, string unit)
    {
        lock (_gate)
        {
            var state = CurrentStateUnsafe();
            var trace = Tracer.Trace(state, readingThreshold, unit);
            return _conclusions.Publish(state, trace, _hypotheses.Current());
        }
    }

    public ExportDocument Export(double readingThreshold, string unit)
    {
        lock (_gate)
        {
            var state = CurrentStateUnsafe();
            var trace = Tracer.Trace(state, readingThreshold, unit);
            return Exporter.Build(state, trace, _hypotheses.Current(), _conclusions);
        }
    }

    public string WriteExport(double readingThreshold, string unit)
    {
        var doc = Export(readingThreshold, unit);
        lock (_gate)
            return Exporter.Write(doc, _dataDir);
    }

    /// <summary>Resume interrupted background work; results are write-once.</summary>
    public int RecoverJobs()
    {
        lock (_gate)
            return _jobs.RunPending(job => job.Kind switch
            {
                "recompute" => $"events={_store.RawLog().Count}",
                _ => "noop",
            });
    }

    private LabState CurrentStateUnsafe() => LabState.Build(_store.EffectiveEvents());
}
