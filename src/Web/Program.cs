using System.Text.Json;
using PairwiseGsb.Core;

var builder = WebApplication.CreateBuilder(args);
var dataDir = builder.Configuration["DATA_DIR"]
    ?? Path.Combine(AppContext.BaseDirectory, "data");
builder.Services.AddSingleton(new LabService(dataDir));
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", (LabService svc) =>
{
    var s = svc.CurrentState();
    return Results.Ok(new
    {
        ruleVersion = s.RuleVersion,
        conclusionStatus = svc.Conclusions.Status.ToString(),
        missingFreezes = svc.Conclusions.MissingFreezes(s),
        plates = s.Plates.Values,
        reagentBatches = s.ReagentBatches.Values,
        frozenEntities = s.FrozenEntities,
        conversions = s.Conversions,
        errors = s.Errors,
        wells = s.Wells.Values.Select(w => new
        {
            well = w.Ref.ToString(),
            w.Alias,
            initialVolume = w.InitialVolume.ToString(),
            currentVolume = w.CurrentVolume.ToString(),
            readings = w.Readings,
            incoming = w.Incoming.Select(e => e.EventId),
            outgoing = w.Outgoing.Select(e => e.EventId),
        }),
        edges = s.Edges,
        jobs = svc.Jobs.Jobs,
    });
});

app.MapGet("/api/events", (LabService svc) => Results.Ok(new
{
    raw = svc.Store.RawLog().Select(e => new { e.EventId, e.Timestamp, type = e.GetType().Name }),
    effective = svc.Store.EffectiveEvents().Select(e => new { e.EventId, e.Timestamp, type = e.GetType().Name }),
    batches = svc.Store.Batches.Select(b => new { b.IdempotencyKey, b.CommittedAt, count = b.Events.Count }),
}));

app.MapPost("/api/batches", async (HttpContext ctx, LabService svc) =>
{
    var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (key is null)
        return Results.BadRequest(new { error = "Idempotency-Key header is required" });
    List<LabEvent>? events;
    try
    {
        events = await JsonSerializer.DeserializeAsync<List<LabEvent>>(ctx.Request.Body, RuleSet.Json);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    if (events is null) return Results.BadRequest(new { error = "Body must be a JSON array of events" });
    var result = svc.SubmitBatch(key, events);
    svc.RecoverJobs();
    return result.RejectionReasons.Count > 0
        ? Results.UnprocessableEntity(result)
        : Results.Ok(result);
});

app.MapGet("/api/trace", (double threshold, string unit, double? minFraction, LabService svc) =>
    Results.Ok(svc.Trace(threshold, unit ?? "RFU", minFraction ?? 0.0)));

app.MapGet("/api/hypotheses", (LabService svc) => Results.Ok(new
{
    current = svc.Hypotheses.Current(),
    history = svc.Hypotheses.History,
}));

app.MapPost("/api/hypotheses", async (HttpContext ctx, LabService svc) =>
{
    var h = await JsonSerializer.DeserializeAsync<Hypothesis>(ctx.Request.Body, RuleSet.Json);
    return h is null ? Results.BadRequest(new { error = "Invalid hypothesis" })
        : Results.Ok(svc.UpsertHypothesis(h));
});

app.MapGet("/api/analysis", (double threshold, string unit, LabService svc) =>
    Results.Ok(svc.AnalyzeHypotheses(threshold, unit ?? "RFU")));

app.MapPost("/api/publish", (HttpContext ctx, LabService svc) =>
{
    var q = ctx.Request.Query;
    var threshold = double.TryParse(q["threshold"], out var t) ? t : 1000.0;
    var unit = q["unit"].FirstOrDefault() ?? "RFU";
    try
    {
        return Results.Ok(svc.Publish(threshold, unit));
    }
    catch (InvalidOperationException ex)
    {
        return Results.UnprocessableEntity(new { error = ex.Message });
    }
});

app.MapGet("/api/conclusion", (LabService svc) => Results.Ok(new
{
    status = svc.Conclusions.Status.ToString(),
    published = svc.Conclusions.Published,
}));

app.MapGet("/api/export", (HttpContext ctx, LabService svc) =>
{
    var q = ctx.Request.Query;
    var threshold = double.TryParse(q["threshold"], out var t) ? t : 1000.0;
    var unit = q["unit"].FirstOrDefault() ?? "RFU";
    var path = svc.WriteExport(threshold, unit);
    return Results.Ok(new { path, document = svc.Export(threshold, unit) });
});

app.Run();

public partial class Program { }
