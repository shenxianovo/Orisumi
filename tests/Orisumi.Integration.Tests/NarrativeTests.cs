using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orisumi.Api.Narrative;
using Orisumi.Application.Data;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class NarrativeTests
{
    private static readonly NarrativeRequest Range = new(DateTimeOffset.Parse("2026-10-11T00:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-10-12T00:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture), "Asia/Shanghai");

    [Test, Property("caseId", "web-today")]
    public async Task ValidatesRangeAndConfigurationBeforeStarting()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString); using var http = factory.CreateClient();
        foreach (var range in new[] { Range with { To = Range.From }, Range with { To = Range.From.AddHours(26) }, Range with { TimeZone = "invalid/zone" } })
        {
            using var response = await http.PostAsJsonAsync("narratives", range);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        using var extra = await http.PostAsJsonAsync("narratives", new { Range.From, Range.To, Range.TimeZone, extra = true });
        await Assert.That(extra.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var missing = await http.PostAsJsonAsync("narratives", new { to = "0001-01-01T01:00:00Z", timeZone = "UTC" });
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var unavailable = await http.PostAsJsonAsync("narratives", Range);
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await WriteClient.JsonAsync(unavailable)).GetProperty("code").GetString()).IsEqualTo("narrative_unavailable");
    }

    [Test, Property("caseId", "web-today")]
    public async Task EmptyDayDoesNotCallModelAndObserverIdentitySurvivesRestart()
    {
        await using var db = await DatabaseFixture.CreateAsync(); var model = new FakeModel(); Guid id;
        await using (var factory = Factory(db.ConnectionString, model))
        {
            using var http = factory.CreateClient();
            using var response = await http.PostAsJsonAsync("narratives", Range);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("event: saved\ndata: {\"observationIds\":[]}\n\n");
            await Assert.That(model.Calls).IsEqualTo(0);
            var page = await http.GetFromJsonAsync<JsonElement>("observations?resourceName=observer-descriptions");
            id = page.GetProperty("observations")[0].GetProperty("observerId").GetGuid();
        }
        await using var restarted = Factory(db.ConnectionString, model); using var client = restarted.CreateClient();
        var descriptions = await client.GetFromJsonAsync<JsonElement>("observations?resourceName=observer-descriptions");
        await Assert.That(descriptions.GetProperty("observations").GetArrayLength()).IsEqualTo(1);
        await Assert.That(descriptions.GetProperty("observations")[0].GetProperty("observerId").GetGuid()).IsEqualTo(id);
    }

    [Test, Property("caseId", "web-today")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamsBeforeCompletionRejectsOverlapAndPersistsAfterDisconnect(bool disconnect)
    {
        await using var db = await DatabaseFixture.CreateAsync(); var model = new FakeModel { Pause = true };
        await using var factory = Factory(db.ConnectionString, model); using var http = factory.CreateClient();
        var foreground = await Seed(http); var period = (await foreground.Observations()).Single();
        var app = period.Content.GetProperty("localApplicationId").GetGuid();
        model.Lines = [Line(app), Line(app, [Guid.CreateVersion7()]), Line(app, title: "第二件事")];
        using var request = new HttpRequestMessage(HttpMethod.Post, "narratives") { Content = JsonContent.Create(Range) };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        await Assert.That(await reader.ReadLineAsync(cancellation.Token)).IsEqualTo("event: episode");
        await Assert.That((await reader.ReadLineAsync(cancellation.Token))!.Contains("+08:00", StringComparison.Ordinal)).IsTrue();
        using var overlap = await http.PostAsJsonAsync("narratives", Range);
        await Assert.That(overlap.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await WriteClient.JsonAsync(overlap)).GetProperty("code").GetString()).IsEqualTo("narrative_in_progress");
        await Assert.That((await foreground.Data.ListAsync("observations?resourceName=episodes")).Count).IsEqualTo(0);
        if (disconnect) { await cancellation.CancelAsync(); response.Dispose(); }
        model.Resume.TrySetResult();
        if (!disconnect) await Assert.That(await reader.ReadToEndAsync()).Contains("event: saved");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IReadOnlyList<JsonElement> episodes;
        do { episodes = await foreground.Data.ListAsync("observations?resourceName=episodes&view=current", timeout.Token); if (episodes.Count == 0) await Task.Delay(20, timeout.Token); } while (episodes.Count == 0);
        await Assert.That(episodes.Count).IsEqualTo(2);
        var episode = episodes[0];
        await Assert.That(episode.GetProperty("formation").GetString()).IsEqualTo("inferred");
        await Assert.That(episode.GetProperty("timeZone").GetString()).IsEqualTo("Asia/Shanghai");
        var inputs = await foreground.Data.GetAsync($"input-sets/{episode.GetProperty("inputSetId").GetGuid()}");
        await Assert.That(inputs.GetProperty("members").EnumerateArray().Any(v => v.GetGuid() == period.ObservationId)).IsTrue();
    }

    [Test, Property("caseId", "web-today")]
    public async Task MidStreamFailureSavesNothingAndReleasesTheRange()
    {
        await using var db = await DatabaseFixture.CreateAsync(); var model = new FakeModel { Fail = true };
        await using var factory = Factory(db.ConnectionString, model); using var http = factory.CreateClient();
        var foreground = await Seed(http);
        var app = (await foreground.Observations()).Single().Content.GetProperty("localApplicationId").GetGuid();
        model.Lines = [Line(app)];
        using var response = await http.PostAsJsonAsync("narratives", Range);
        var stream = await response.Content.ReadAsStringAsync();
        await Assert.That(stream).Contains("event: episode"); await Assert.That(stream).Contains("event: failed");
        await Assert.That((await foreground.Data.ListAsync("observations?resourceName=episodes")).Count).IsEqualTo(0);
        model.Fail = false;
        using var retry = await http.PostAsJsonAsync("narratives", Range);
        await Assert.That(await retry.Content.ReadAsStringAsync()).Contains("event: saved");
    }

    [Test, Property("caseId", "web-today")]
    public async Task RewriteRespectsConfirmationAndReadsDenials()
    {
        await using var db = await DatabaseFixture.CreateAsync(); var model = new FakeModel();
        await using var factory = Factory(db.ConnectionString, model); using var http = factory.CreateClient();
        var foreground = await Seed(http);
        var app = (await foreground.Observations()).Single().Content.GetProperty("localApplicationId").GetGuid();
        model.Lines = [Line(app), Line(app, title: "保留"), Line(app, title: "重启项目栈")];
        using var first = await http.PostAsJsonAsync("narratives", Range);
        var previous = await foreground.Data.ListAsync("observations?resourceName=episodes");
        await using var person = new DataClient(http, http, Ref.Recognize(BuiltInSchemas.Person, new(null, "person", "self")));
        await person.WriteAsync(new(CommonSchemas.Review, new { targetId = previous[1].GetProperty("observationId").GetGuid(), scope = (object?)null, verdict = "confirm", text = (string?)null }, "stated"));
        await person.WriteAsync(new(CommonSchemas.Review, new { targetId = previous[2].GetProperty("observationId").GetGuid(), scope = (object?)null, verdict = "deny", text = "不是重启项目栈" }, "stated"));
        model.Lines = [Line(app, title: "新的一件事")];
        using var next = await http.PostAsJsonAsync("narratives", Range);
        await Assert.That(await next.Content.ReadAsStringAsync()).Contains("event: saved");
        await Assert.That(JsonSerializer.Serialize(model.Input, NarrativeData.Json)).Contains("denials");
        var current = await foreground.Data.ListAsync("observations?resourceName=episodes&view=current");
        await Assert.That(current.Count).IsEqualTo(2);
        var reviews = await foreground.Data.ListAsync("observations?resourceName=reviews");
        await Assert.That(reviews.Count).IsEqualTo(2);
        await Assert.That(current.Any(item => item.GetProperty("replaces").GetArrayLength() > 0)).IsTrue();
    }

    [Test, Property("caseId", "api-writes")]
    public async Task AtomicWriterRollsBackEarlierItemsWhenLaterWriteIsRejected()
    {
        await using var db = await DatabaseFixture.CreateAsync(); var model = new FakeModel();
        await using var factory = Factory(db.ConnectionString, model); using var http = factory.CreateClient();
        var foreground = await Seed(http); var app = (await foreground.Observations()).Single().Content.GetProperty("localApplicationId").GetGuid();
        using var scope = factory.Services.CreateScope();
        var writes = new[] { new ObservationWrite(BuiltInSchemas.Name, JsonSerializer.SerializeToElement(new { thingId = app, name = "valid" }), "inferred"),
            new ObservationWrite(BuiltInSchemas.Name, JsonSerializer.SerializeToElement(new { thingId = Guid.CreateVersion7(), name = "invalid" }), "inferred") };
        await Assert.That(async () => await scope.ServiceProvider.GetRequiredService<WriteService>().WriteAtomicAsync(new(NarrativeData.Observer, writes, []))).Throws<ModelViolationException>();
        await Assert.That((await foreground.Data.ListAsync("observations?resourceName=names")).Any(v => v.GetProperty("content").GetProperty("name").GetString() == "valid")).IsFalse();
    }
    private static OrisumiApiFactory Factory(string connection, FakeModel model) => new(connection, configureServices: s => s.AddSingleton<INarrativeModel>(model));
    private static async Task<ForegroundTestStore> Seed(HttpClient http)
    {
        var f = await ForegroundTestStore.CreateAsync(http); f.Store.Checkpoint(f.Capture(), null); f.Store.FinishActive(); return f;
    }
    private static string Line(Guid app, Guid[]? sessions = null, string title = "写代码") => JsonSerializer.Serialize(new { start = "09:00", end = "10:00", title, summary = "你完成了代码。", sessionIds = sessions ?? [], localApplicationIds = new[] { app } });
    private sealed class FakeModel : INarrativeModel
    {
        public bool IsConfigured => true;
        public string Model => "test-model";
        internal string[] Lines { get; set; } = [];
        internal bool Pause { get; set; }
        internal bool Fail { get; set; }
        internal int Calls { get; private set; }
        internal object? Input { get; private set; }
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<string> LinesAsync(object input, [EnumeratorCancellation] CancellationToken token)
        {
            Calls++; Input = input;
            for (var i = 0; i < Lines.Length; i++) { yield return Lines[i]; if (Pause && i == 0) await Resume.Task.WaitAsync(token); }
            if (Fail) throw new HttpRequestException("model disconnected");
        }
    }
}
