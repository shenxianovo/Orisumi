using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Observer.Sdk;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class ReadingTests
{
    [Test, Property("caseId", "api-runs"), Property("caseId", "api-search"), Property("caseId", "api-thing-view")]
    public async Task RunsCaptureOnlyReturnedObservationsAndEachWriteSnapshotsReads()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: WriteApiTests.NameFields.Replace("\"type\":\"string\"", "\"type\":\"string\",\"searchable\":true", StringComparison.Ordinal));
        var one = await writer.WriteAsync(schema, new { name = "中文 needle" });
        var two = await writer.WriteAsync(schema, new { name = "second" });
        using var created = await http.PostAsJsonAsync("runs", new { });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var run = (await WriteClient.JsonAsync(created)).GetProperty("runId").GetGuid();
        var empty = await writer.WriteAsync(schema, new { name = "empty run" }, runId: run);
        await Assert.That(empty.InputSetId).IsNull();
        await http.GetFromJsonAsync<JsonElement>($"observations?resourceName=items&limit=1&runId={run}");
        var first = await writer.WriteAsync(schema, new { name = "first snapshot" }, runId: run);
        await Assert.That(await Members(first.InputSetId!.Value)).IsEquivalentTo([one.ObservationId]);
        await http.GetFromJsonAsync<JsonElement>($"observations/{two.ObservationId}"); // No run: excluded.
        await Assert.That(await Members((await writer.WriteAsync(schema, new { name = "unchanged reads" }, runId: run)).InputSetId!.Value)).IsEquivalentTo([one.ObservationId]);
        await http.GetFromJsonAsync<JsonElement>($"observations/{two.ObservationId}?runId={run}");
        var next = await writer.WriteAsync(schema, new { name = "next snapshot" }, runId: run);
        await Assert.That(await Members(next.InputSetId!.Value)).IsEquivalentTo([one.ObservationId, two.ObservationId]);
        await Assert.That(await Members(first.InputSetId!.Value)).IsEquivalentTo([one.ObservationId]);
        var page = await http.GetFromJsonAsync<JsonElement>("search?q=中文&resourceName=items");
        await Assert.That(page.GetProperty("observations")[0].GetProperty("observationId").GetGuid()).IsEqualTo(one.ObservationId);
        using var emptyQuery = await http.GetAsync("search?q=");
        await Assert.That(emptyQuery.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var invalid = await http.GetAsync($"search?q=中文&runId={Guid.CreateVersion7()}");
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var both = await http.PostAsJsonAsync("writes", new { observer = writer.ObserverReference, writes = new[] { WriteClient.Write(schema, new { name = "bad" }, inputs: [], runId: run) } });
        await Assert.That(both.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        async Task<Guid[]> Members(Guid id) => (await http.GetFromJsonAsync<JsonElement>("input-sets/" + id)).GetProperty("members").EnumerateArray().Select(value => value.GetGuid()).ToArray();
    }

    [Test, Property("caseId", "api-thing-view"), Property("caseId", "api-reading"), Property("caseId", "api-runs")]
    public async Task ObjectViewClassifiesEveryDeclaredRoleAndReadRecordsIncludeItsReturnedState()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var type = await writer.DeclareThingAsync("objects");
        var name = await writer.WriteAsync(CommonSchemas.Name, new { thingId = Ref.Recognize(type, new(null, "test", "object")), name = "object" });
        var thing = name.Content.GetProperty("thingId").GetGuid();
        var relation = await writer.WriteAsync(CommonSchemas.Relation, new { subjectId = thing, predicate = "belongs to", objectId = writer.ObserverId, text = (string?)null });
        var schema = await writer.DeclareAsync("events", """{"partId":{"type":"reference","target":"thing","role":"part","description":"Part","required":true,"nullable":false},"mentionId":{"type":"reference","target":"thing","role":"mention","description":"Mention","required":true,"nullable":false}}""");
        var activity = await writer.WriteAsync(schema, new { partId = thing, mentionId = thing });
        var run = (await WriteClient.JsonAsync(await http.PostAsJsonAsync("runs", new { }))).GetProperty("runId").GetGuid();
        var view = await http.GetFromJsonAsync<JsonElement>($"things/{thing}?runId={run}");
        await Assert.That(view.GetProperty("state")[0].GetProperty("observationId").GetGuid()).IsEqualTo(name.ObservationId);
        await Assert.That(view.GetProperty("relations")[0].GetProperty("observationId").GetGuid()).IsEqualTo(relation.ObservationId);
        await Assert.That(view.GetProperty("activity").GetArrayLength()).IsEqualTo(2);
        foreach (var item in view.GetProperty("activity").EnumerateArray())
        {
            var page = await http.GetFromJsonAsync<JsonElement>(item.GetProperty("href").GetString());
            await Assert.That(page.GetProperty("observations").GetArrayLength()).IsEqualTo(1);
            await Assert.That(page.GetProperty("observations")[0].GetProperty("observationId").GetGuid()).IsEqualTo(activity.ObservationId);
        }
        var derived = await writer.WriteAsync(CommonSchemas.Name, new { thingId = thing, name = "derived" }, runId: run);
        var inputs = await http.GetFromJsonAsync<JsonElement>("input-sets/" + derived.InputSetId);
        await Assert.That(inputs.GetProperty("members").EnumerateArray().Select(item => item.GetGuid()).ToHashSet().SetEquals([name.ObservationId, relation.ObservationId])).IsTrue();
        await using var data = new DataClient(http, http, Ref.Saved(writer.ObserverId));
        var model = Ref.Recognize(CommonSchemas.Model, new(null, "orisumi.model", "test-model"));
        await data.WriteAsync(new(schema, new { partId = thing, mentionId = thing }, "direct", SpeakerId: model));
        var excluded = await data.ListAsync("observations?resourceName=events&excludeSpeakerType=models");
        await Assert.That(excluded.Count).IsEqualTo(1);
        await Assert.That((await data.ListAsync($"observations?resourceName=events&speakerId={writer.ObserverId}")).Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "api-same-as"), Property("caseId", "api-thing-view")]
    public async Task SameAsExpandsTransitivelyInItsTimeAndDenialFollowsTheUnorderedPair()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var type = await writer.DeclareThingAsync("objects");
        var things = new List<Guid>();
        foreach (var key in new[] { "a", "b", "c" })
        {
            var name = await writer.WriteAsync(CommonSchemas.Name, new { thingId = Ref.Recognize(type, new(null, "test", key)), name = key });
            things.Add(name.Content.GetProperty("thingId").GetGuid());
        }
        var schema = await writer.DeclareAsync("events", """{"objectId":{"type":"reference","target":"thing","role":"part","description":"Object","required":true,"nullable":false}}""");
        foreach (var thing in things) await writer.WriteAsync(schema, new { objectId = thing }, time: UnderstandingApiTests.Time(1, 2));
        var outside = await writer.WriteAsync(schema, new { objectId = things[1] }, time: UnderstandingApiTests.Time(3, 4));
        var link = await writer.WriteAsync(CommonSchemas.SameAs, new { leftId = things[0], rightId = things[1] }, time: UnderstandingApiTests.Time(0, 3));
        await writer.WriteAsync(CommonSchemas.SameAs, new { leftId = things[1], rightId = things[2] });
        await Assert.That((await Events()).Length).IsEqualTo(3);
        var view = await http.GetFromJsonAsync<JsonElement>("things/" + things[0]);
        await Assert.That(view.GetProperty("sameAsThingIds").GetArrayLength()).IsEqualTo(2);
        await Assert.That(view.GetProperty("activity")[0].GetProperty("count").GetInt64()).IsEqualTo(3);
        var refs = await http.GetFromJsonAsync<JsonElement>($"things/{things[0]}/referrers?resourceName=events");
        await Assert.That(refs.GetProperty("referrers").GetArrayLength()).IsEqualTo(1);
        await writer.WriteAsync(CommonSchemas.Review, new { targetId = link.ObservationId, scope = (object?)null, verdict = "deny", text = (string?)null }, time: UnderstandingApiTests.Time(1, 2), formation: "stated");
        await Assert.That((await Events()).Length).IsEqualTo(1);
        await writer.WriteAsync(schema, new { objectId = things[1] }, time: UnderstandingApiTests.Time(2, 3));
        await Assert.That((await Events()).Length).IsEqualTo(2);
        await writer.WriteAsync(CommonSchemas.SameAs, new { leftId = things[1], rightId = things[0] }, time: UnderstandingApiTests.Time(0, 3));
        await writer.WriteAsync(CommonSchemas.Review, new { targetId = link.ObservationId, scope = (object?)null, verdict = "deny", text = (string?)null }, formation: "stated");
        await Assert.That((await Events()).Length).IsEqualTo(1);
        async Task<JsonElement[]> Events() => (await http.GetFromJsonAsync<JsonElement>($"observations?thingId={things[0]}&resourceName=events")).GetProperty("observations").EnumerateArray().ToArray();
    }
}
