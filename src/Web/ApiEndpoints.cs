using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PlateTrace.Core.Engine;
using PlateTrace.Core.Jobs;
using PlateTrace.Core.Models;
using PlateTrace.Core.Store;

namespace PlateTrace.Web;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/state", (TraceService svc) =>
        {
            var p = svc.Projection;
            return Results.Ok(new
            {
                ruleVersion = RuleVersion.Current,
                fingerprint = p.Fingerprint,
                lastSeq = p.LastSeq,
                plates = p.Plates.Values,
                reagentBatches = p.ReagentBatches.Values,
                tips = p.Tips.Values,
                errors = p.Errors,
                corrections = p.Corrections.Select(c => new
                {
                    c.EventId, c.OccurredAt, c.SupersedesEventId, c.Reason, c.BatchId,
                }),
                wells = p.Wells.Values.Select(w => new
                {
                    wellId = w.Ref.Id, plateId = w.Ref.PlateId, well = w.Ref.Well,
                    sampleAlias = w.SampleAlias,
                    volumeLow = w.Volume.Low, volumeHigh = w.Volume.High,
                    reagentBatches = w.ReagentBatches,
                    lastMixAt = w.LastMixAt,
                    readings = w.Readings,
                    abnormal = w.Readings.Any(r => r.IsAbnormal),
                }),
            });
        });

        api.MapGet("/timeline", async (TraceService svc, CancellationToken ct) =>
        {
            var batches = await svc.ListBatchesAsync(ct);
            return Results.Ok(new
            {
                rawLog = batches.OrderBy(b => b.CommittedAt).Select(b => new
                {
                    b.BatchId, b.IdempotencyKey, b.Outcome, b.CommittedAt, b.Note, b.RuleVersion,
                    events = b.Events.Select(e => new
                    {
                        e.Seq, e.EventId, e.Kind, e.OccurredAt,
                        plate = e.PlateId, well = e.Well,
                        from = e.FromPlate is null ? null : $"{e.FromPlate}/{e.FromWell}",
                        to = e.ToPlate is null ? null : $"{e.ToPlate}/{e.ToWell}",
                        tip = e.TipId, reagent = e.ReagentBatch, analyte = e.Analyte,
                        volume = e.VolumeLow is null ? null : new { low = e.VolumeLow, high = e.VolumeHigh },
                        aspirate = e.AspirateLow is null ? null : new { low = e.AspirateLow, high = e.AspirateHigh },
                        value = e.Value, unit = e.Unit, threshold = e.ThresholdHigh,
                        supersedes = e.SupersedesEventId, reason = e.Reason,
                    }),
                    errors = b.Errors,
                }),
                workingTimeline = svc.Projection.Timeline.Select(e => new
                {
                    e.Seq, e.EventId, e.Kind, e.OccurredAt, isReplacement = e.IsReplacement,
                    supersededBy = e.SupersedesEventId,
                }),
                supersededEventIds = svc.Projection.SupersededEventIds,
            });
        });

        api.MapPost("/batches", async (BatchSubmission submission, TraceService svc, CancellationToken ct) =>
        {
            var result = await svc.SubmitAsync(submission, ct);
            return Results.Json(new
            {
                accepted = result.Accepted,
                idempotentReplay = result.IdempotentReplay,
                batchId = result.Batch.BatchId,
                outcome = result.Batch.Outcome.ToString(),
                errors = result.Batch.Errors,
                events = result.Batch.Events.Select(e => new { e.EventId, e.Seq, e.Kind }),
            }, statusCode: result.Accepted ? 200 : 422);
        });

        api.MapGet("/batches", async (TraceService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListBatchesAsync(ct)));

        api.MapGet("/graph", (TraceService svc, double? minFraction) =>
        {
            var p = svc.Projection;
            var tracer = new PathTracer(p, minFraction ?? svc.PathMinFraction);
            return Results.Ok(new
            {
                minFraction = minFraction ?? svc.PathMinFraction,
                edges = p.TransferEdges,
                reagentEdges = p.ReagentEdges,
                paths = tracer.AllPaths().Select(path => new
                {
                    origin = path.Origin.Id, target = path.Target.Id,
                    dilutionLow = path.Cumulative.Low, dilutionHigh = path.Cumulative.High,
                    edges = path.Edges.Select(e => new { e.EventId, e.Label, tip = e.TipId }),
                }),
            });
        });

        api.MapGet("/wells/{plateId}/{well}/paths", (string plateId, string well, TraceService svc, double? minFraction) =>
        {
            var target = new WellRef(plateId, well.ToUpperInvariant());
            var tracer = new PathTracer(svc.Projection, minFraction ?? svc.PathMinFraction);
            var paths = tracer.PathsInto(target);
            return Results.Ok(new
            {
                target = target.Id,
                minFraction = minFraction ?? svc.PathMinFraction,
                paths = paths.Select(path => new
                {
                    origin = path.Origin.Id,
                    dilutionLow = path.Cumulative.Low, dilutionHigh = path.Cumulative.High,
                    edges = path.Edges.Select(e => new { e.EventId, e.Label, tip = e.TipId }),
                }),
            });
        });

        api.MapGet("/readings/compare", (TraceService svc) => Results.Ok(svc.BuildReadingComparisons()));

        // hypotheses (candidate layer) -----------------------------------------
        api.MapGet("/hypotheses", async (TraceService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListHypothesesAsync(ct)));

        api.MapPost("/hypotheses", async (HypothesisRequest req, TraceService svc, JobRunner jobs, CancellationToken ct) =>
        {
            try
            {
                var saved = await svc.SaveHypothesisAsync(req, ct);
                var job = await jobs.EnqueueEvaluationAsync(saved, ct);
                return Results.Json(new { hypothesis = saved, jobId = job.JobId }, statusCode: 202);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPost("/hypotheses/{id}/retire", async (string id, HttpRequest request, TraceService svc, CancellationToken ct) =>
        {
            Dictionary<string, string>? body = null;
            if (request.ContentLength is > 0)
                body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(request.Body,
                    cancellationToken: ct);
            var retired = await svc.RetireHypothesisAsync(id, body?.GetValueOrDefault("reason") ?? "retired", ct);
            return Results.Ok(retired);
        });

        api.MapGet("/jobs", async (JobRunner jobs, TraceService svc, CancellationToken ct) =>
        {
            var jobList = await svc.ListJobsAsync(ct);
            return Results.Ok(jobList.Select(j => new
            {
                j.JobId, j.Kind, j.State, j.Attempts, j.IdempotencyKey,
                j.CreatedAt, j.StartedAt, j.FinishedAt, j.Error,
                result = string.IsNullOrEmpty(j.ResultJson)
                    ? (JsonElement?)null
                    : JsonSerializer.Deserialize<JsonElement>(j.ResultJson),
            }));
        });

        api.MapPost("/jobs/pump", async (JobRunner jobs, CancellationToken ct) =>
            Results.Ok(new { processed = await jobs.PumpAsync(ct) }));

        // conclusions ----------------------------------------------------------
        api.MapGet("/conclusions", async (TraceService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListConclusionsAsync(ct)));

        api.MapPost("/conclusions", async (PublishRequest req, TraceService svc, CancellationToken ct) =>
        {
            var result = await svc.PublishConclusionAsync(req, ct);
            return result.Published
                ? Results.Ok(result.Conclusion)
                : Results.Json(new { published = false, errors = result.Errors }, statusCode: 422);
        });

        api.MapGet("/export", async (TraceService svc, CancellationToken ct) =>
        {
            var bundle = await svc.BuildExportBundleAsync(null, ct);
            return Results.Json(bundle, contentType: "application/json");
        });

        api.MapPost("/reset-demo", async (TraceService svc, CancellationToken ct) =>
        {
            // test/demo helper: wipes the data directory and replays the demo
            var dir = svc.Store.DataDir;
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name is StoreFormat.LockFile or StoreFormat.FormatFile) continue;
                File.Delete(f);
            }
            await svc.InitializeAsync(ct);
            await DemoSeeder.SeedAsync(svc, ct);
            return Results.Ok(new { reset = true });
        });

        return app;
    }
}
