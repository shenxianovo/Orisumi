using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Observer.Sdk;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class DeletionTests
{
    [Test, Property("caseId", "api-deletion")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DeletionFollowsReplacementAncestorsAndReportsSurvivingDependents(bool deleteLatest)
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: WriteApiTests.NameFields);
        var first = await writer.WriteAsync(schema, new { name = "a" }, "chain");
        var second = await writer.WriteAsync(schema, new { name = "b" }, "chain");
        var third = await writer.WriteAsync(schema, new { name = "c" }, "chain");
        var dependent = await writer.WriteAsync(schema, new { name = "derived" }, inputs: [first.ObservationId], formation: "inferred");
        using var deleted = await http.DeleteAsync("observations/" + (deleteLatest ? third : second).ObservationId);
        var result = await WriteClient.JsonAsync(deleted);
        await Assert.That(result.GetProperty("deleted").EnumerateArray().Select(item => item.GetGuid()).ToHashSet()
            .SetEquals(deleteLatest ? [first.ObservationId, second.ObservationId, third.ObservationId] : [first.ObservationId, second.ObservationId])).IsTrue();
        await Assert.That(result.GetProperty("dependents")[0].GetGuid()).IsEqualTo(dependent.ObservationId);
        var inputs = await http.GetFromJsonAsync<JsonElement>($"input-sets/{dependent.InputSetId}");
        await Assert.That(inputs.GetProperty("members")[0].GetGuid()).IsEqualTo(first.ObservationId);
        using var missing = await http.GetAsync("observations/" + first.ObservationId);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        using var newest = await http.GetAsync("observations/" + third.ObservationId);
        await Assert.That(newest.StatusCode).IsEqualTo(deleteLatest ? HttpStatusCode.Gone : HttpStatusCode.OK);
        using var again = await http.DeleteAsync("observations/" + second.ObservationId);
        await Assert.That((await WriteClient.JsonAsync(again)).GetProperty("deleted").GetArrayLength()).IsEqualTo(0);
        if (deleteLatest)
            foreach (var name in new[] { "a", "different" })
                await Assert.That((await writer.SendAsync([WriteClient.Write(schema, new { name }, "chain")])).GetProperty("results")[0].GetProperty("skipped").GetBoolean()).IsTrue();
        else
            await Assert.That((await writer.WriteAsync(schema, new { name = "c" }, "chain")).ObservationId).IsEqualTo(third.ObservationId);
    }

    [Test, Property("caseId", "api-deletion")]
    public async Task ThingDeletionRemovesAboutAndPartButRetainsMentionsAndCoreReferences()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var thingType = await writer.DeclareThingAsync("objects");
        var reference = Ref.Recognize(thingType, new(null, "native", "private-source-key"));
        var schemas = new Dictionary<string, Guid>();
        var observations = new Dictionary<string, Guid>();
        Guid thing = default;
        foreach (var role in new[] { "about", "part", "mention" })
        {
            var schema = await writer.DeclareAsync(role + "-items", """{"objectId":{"type":"reference","target":"thing","role":"ROLE","description":"Object","required":true,"nullable":false}}""".Replace("ROLE", role, StringComparison.Ordinal));
            var observation = await writer.WriteAsync(schema, new { objectId = reference });
            thing = observation.Content.GetProperty("objectId").GetGuid();
            schemas[role] = schema; observations[role] = observation.ObservationId;
        }
        await using var spoken = new DataClient(http, http, Ref.Saved(thing));
        var core = (await spoken.WriteAsync(new(CommonSchemas.Name, new { thingId = writer.ObserverId, name = "spoken" }, "stated", SpeakerId: Ref.Saved(thing))))!;
        var derived = await writer.WriteAsync(schemas["mention"], new { objectId = reference }, inputs: [observations["part"]]);
        using var response = await http.DeleteAsync("things/" + thing);
        var result = await WriteClient.JsonAsync(response);
        await Assert.That(result.GetProperty("deleted").EnumerateArray().Select(item => item.GetGuid()).ToHashSet().SetEquals([thing, observations["about"], observations["part"]])).IsTrue();
        await Assert.That(result.GetProperty("dependents")[0].GetGuid()).IsEqualTo(derived.ObservationId);
        foreach (var id in new[] { observations["mention"], core.Observation!.Value.GetProperty("observationId").GetGuid() })
        { using var retained = await http.GetAsync("observations/" + id); retained.EnsureSuccessStatusCode(); }
        foreach (var role in new[] { "about", "part" })
            await Assert.That((await writer.SendAsync([WriteClient.Write(schemas[role], new { objectId = reference })])).GetProperty("results")[0].GetProperty("skipped").GetBoolean()).IsTrue();
        var mention = await writer.WriteAsync(schemas["mention"], new { objectId = reference });
        await Assert.That(mention.Content.GetProperty("objectId").GetGuid()).IsEqualTo(thing);
        var rejected = await writer.SendAsync([WriteClient.Write(schemas["mention"], new { objectId = thing })]);
        await Assert.That(rejected.GetProperty("results")[0].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("REFERENCE-3");
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "key_tombstones")).IsEqualTo(1);
        using var missing = await http.GetAsync("things/" + thing);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.Gone);
    }
}
