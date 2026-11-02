using System.Text.Json;
using Orisumi.Application.Data;
using Orisumi.Core;

namespace Orisumi.Api.Narrative;

internal sealed partial class NarrativeService(IServiceScopeFactory scopes, INarrativeModel model,
    IHostApplicationLifetime lifetime, ILogger<NarrativeService> logger) : IHostedService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Narrative generation failed for {From} to {To}")]
    private static partial void LogFailure(ILogger logger, Exception exception, DateTimeOffset from, DateTimeOffset to);

    private readonly Lock gate = new();
    private readonly List<NarrativeRequest> active = [];
    internal bool IsConfigured => model.IsConfigured;
    internal bool TryBegin(NarrativeRequest request)
    {
        lock (gate)
        {
            if (active.Any(r => r.From < request.To && request.From < r.To)) return false;
            active.Add(request); return true;
        }
    }
    internal void End(NarrativeRequest request) { lock (gate) active.Remove(request); }
    private static JsonElement Description(string model) => JsonSerializer.SerializeToElement(new
    { platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux", hostName = Environment.MachineName,
        programVersion = typeof(NarrativeService).Assembly.GetName().Version!.ToString(), model = "openai/" + model, decisionModel = (string?)null });

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WriteService>().WriteAtomicAsync(
            new(NarrativeData.Observer, [], NarrativeData.Schemas, Description: Description(model.Model)), cancellationToken);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task RunAsync(NarrativeRequest request, Func<string, object, Task> emit)
    {
        // Lifetime belongs to the host, not the browser connection or its request scope.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        try
        {
            using var scope = scopes.CreateScope();
            var evidence = await scope.ServiceProvider.GetRequiredService<NarrativeReader>().ReadAsync(request, token);
            if (evidence.Empty) { await emit("saved", new { observationIds = Array.Empty<Guid>() }); return; }
            var episodes = new List<NarrativeEpisode>();
            var used = new HashSet<Guid>();
            await foreach (var line in model.LinesAsync(evidence.Input, token))
            {
                if (NarrativeData.Parse(line, request, evidence, used) is not { } episode) continue;
                episodes.Add(episode); await emit("episode", episode);
            }
            if (episodes.Count == 0) throw new InvalidDataException("模型没有写出有效的事。");
            var writes = episodes.Select(e => new ObservationWrite(NarrativeData.EpisodeSchema,
                JsonSerializer.SerializeToElement(new { e.Title, e.Summary, e.SessionIds, e.LocalApplicationIds }, NarrativeData.Json), "inferred",
                Time: new(e.StartAt, e.EndAt, request.TimeZone), Inputs: evidence.Inputs, Replaces: evidence.Previous.Select(item => item.ObservationId).ToArray())).ToList();
            var saved = await scope.ServiceProvider.GetRequiredService<WriteService>().WriteAtomicAsync(
                new(NarrativeData.Observer, writes, NarrativeData.Schemas, Description: Description(model.Model)), token);
            await emit("saved", new { observationIds = saved.Results.Take(episodes.Count).Select(r => r.Observation!.ObservationId).ToArray() });
        }
        catch (Exception exception)
        {
            LogFailure(logger, exception, request.From, request.To);
            await emit("failed", new { detail = exception is InvalidDataException ? exception.Message : "这一天的几件事没有写成，请稍后重试。" });
        }
        finally { End(request); }
    }
}

internal static class NarrativeEndpoints
{
    internal static void MapNarratives(this WebApplication app) => app.MapPost("/narratives", async (NarrativeRequest request, HttpContext context, NarrativeService service) =>
    {
        request.Zone();
        if (!service.IsConfigured)
        {
            await Results.Problem(statusCode: 503, detail: "OPENAI_API_KEY is not configured.", extensions: new Dictionary<string, object?> { ["code"] = "narrative_unavailable" }).ExecuteAsync(context);
            return;
        }
        if (!service.TryBegin(request))
        {
            await Results.Problem(statusCode: 409, detail: "An overlapping narrative is in progress.", extensions: new Dictionary<string, object?> { ["code"] = "narrative_in_progress" }).ExecuteAsync(context);
            return;
        }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var disconnected = false;
        await service.RunAsync(request, async (name, payload) =>
        {
            if (disconnected || context.RequestAborted.IsCancellationRequested) return;
            try
            {
                await context.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, NarrativeData.Json)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { disconnected = true; }
        });
    }).WithName("writeNarrative");
}
