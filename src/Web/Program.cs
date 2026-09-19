using System.Text.Json.Serialization;
using PlateTrace.Core.Engine;
using PlateTrace.Core.Jobs;
using PlateTrace.Core.Store;
using PlateTrace.Web;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Environment.GetEnvironmentVariable("PLATE_TRACE_DATA")
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");

builder.Services.AddSingleton<ITraceStore>(new FileTraceStore(dataDir));
builder.Services.AddSingleton<TraceService>();
builder.Services.AddSingleton<JobRunner>(sp =>
    new JobRunner(sp.GetRequiredService<ITraceStore>(), () => sp.GetRequiredService<TraceService>()));
builder.Services.AddHostedService<BackgroundJobService>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var svc = scope.ServiceProvider.GetRequiredService<TraceService>();
    await svc.InitializeAsync();
    await DemoSeeder.SeedAsync(svc);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapApi();

app.Run();
