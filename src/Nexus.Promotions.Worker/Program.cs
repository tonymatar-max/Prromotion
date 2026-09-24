using Microsoft.Extensions.Options;
using Nexus.Promotions.B1;
using Nexus.Promotions.Engine;

// Nexus Promotions (APE) Mode B worker (PRD 6.5, FR-40–FR-44).
//   (no argument)  run as a service: poll dbo.APE_Queue every Worker:PollSeconds
//   once           process everything queued now, then exit (operations, tests)
//
// Evaluation: in-process with its own rule cache (default), or through the promotion API when Engine:Url is set,
// so all channels share one rule cache (PRD 6).
// The Service Layer password comes from APE_ServiceLayer__Password or appsettings.Local.json, never the repository.

var once = args.Contains("once", StringComparer.OrdinalIgnoreCase);
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables("APE_");
// One worker per company; a second company on the same machine sets Worker:ServiceName to install its own.
builder.Services.AddWindowsService(o => o.ServiceName = builder.Configuration["Worker:ServiceName"] ?? "Nexus Promotions Worker");

builder.Services.Configure<ServiceLayerOptions>(builder.Configuration.GetSection("ServiceLayer"));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.AddSingleton(sp => new ServiceLayer(sp.GetRequiredService<IOptions<ServiceLayerOptions>>().Value));

var engineUrl = builder.Configuration["Engine:Url"];
if (string.IsNullOrWhiteSpace(engineUrl))
{
    builder.Services.AddSingleton(sp => new B1MasterData(sp.GetRequiredService<ServiceLayer>(),
        sp.GetRequiredService<IOptions<ServiceLayerOptions>>().Value.PriceList));
    builder.Services.AddSingleton<IPromotionSource, ServiceLayerPromotionSource>();
    builder.Services.AddSingleton(new EngineOptions { HashKey = builder.Configuration["Engine:HashKey"] });
    builder.Services.AddSingleton<PromotionStore>();
    builder.Services.AddSingleton<IDocumentEvaluator, LocalDocumentEvaluator>();
    if (!once)
        builder.Services.AddHostedService(sp => new PromotionRefreshService(sp.GetRequiredService<PromotionStore>(),
            TimeSpan.FromSeconds(60), sp.GetRequiredService<ILogger<PromotionRefreshService>>()));
}
else
{
    builder.Services.AddHttpClient<IDocumentEvaluator, HttpDocumentEvaluator>(http =>
    {
        http.BaseAddress = new Uri(engineUrl.TrimEnd('/') + "/");
        if (builder.Configuration["Engine:ApiKey"] is { Length: > 0 } key) http.DefaultRequestHeaders.Add("X-Api-Key", key);
    });
}

builder.Services.AddSingleton(sp => new ModeBProcessor(
    sp.GetRequiredService<ServiceLayer>(),
    sp.GetRequiredService<IOptions<WorkerOptions>>().Value.SqlConnectionString,
    sp.GetRequiredService<IDocumentEvaluator>(),
    msg => sp.GetRequiredService<ILogger<ModeBProcessor>>().LogInformation("{Message}", msg)));
if (!once) builder.Services.AddHostedService<QueueWorker>();

var host = builder.Build();
if (host.Services.GetService<PromotionStore>() is { } store) await store.ReloadAsync();

if (once)
{
    var handled = await host.Services.GetRequiredService<ModeBProcessor>().DrainAsync();
    Console.WriteLine($"Processed {handled} queued document(s).");
    await host.Services.GetRequiredService<ServiceLayer>().LogoutAsync();
    return;
}
await host.RunAsync();

public sealed class WorkerOptions
{
    public string SqlConnectionString { get; set; } = "";
    public int PollSeconds { get; set; } = 2;
}

public sealed class QueueWorker(ModeBProcessor processor, IOptions<WorkerOptions> options, ILogger<QueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            try { await processor.DrainAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Queue poll failed; retrying on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
