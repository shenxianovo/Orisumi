using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class WriteApiTests
{
    internal const string NameFields = """{"name":{"type":"string","description":"Name","required":true,"nullable":false}}""";
    internal static readonly object UnknownTime = new { startAt = (string?)null, endAt = (string?)null, timeZone = (string?)null };

    [Test, Property("caseId", "api-writes")]
    public async Task RecognitionReplacementsAndSequenceFollowTheWriteContract()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var first = await writer.WriteAsync(schema, new { name = "one" }, time: UnknownTime);
        var second = await writer.WriteAsync(schema, new { name = "one" }, time: UnknownTime);
        await Assert.That(first.ObservationId != second.ObservationId).IsTrue();
        using var readings = await http.GetAsync("/observations?resourceName=items");
        await Assert.That((await WriteClient.JsonAsync(readings)).GetProperty("observations").GetArrayLength()).IsEqualTo(2);
        var keyed = await writer.WriteAsync(schema, new { name = "one" }, "key", time: UnknownTime);
        var same = await writer.SendAsync([WriteClient.Write(schema, new { name = "one" }, "key", time: UnknownTime)]);
        await Assert.That(same.GetProperty("results")[0].GetProperty("changed").GetBoolean()).IsFalse();
        var observation = ObservationJson.ReadSaved(same.GetProperty("results")[0].GetProperty("observation"));
        var repeated = await writer.SendAsync([WriteClient.Write(schema, new { name = "one" }, "key", time: UnknownTime)]);
        await Assert.That(repeated.GetProperty("results")[0].GetProperty("observation").GetProperty("observationId").GetGuid()).IsEqualTo(observation.ObservationId);
        var changed = await writer.WriteAsync(schema, new { name = "two" }, "key");
        var reverted = await writer.WriteAsync(schema, new { name = "one" }, "key");
        await Assert.That(changed.Replaces.SingleOrDefault()).IsEqualTo(keyed.ObservationId);
        await Assert.That(reverted.Replaces.SingleOrDefault()).IsEqualTo(changed.ObservationId);
        var timed = await writer.SendAsync([WriteClient.Write(schema, new { name = "one" }, "key", time: new { startAt = "2026-10-31T00:00:00Z", endAt = (string?)null, timeZone = "Asia/Shanghai" })]);
        await Assert.That(timed.GetProperty("results")[0].GetProperty("observation").GetProperty("replaces")[0].GetGuid()).IsEqualTo(reverted.ObservationId);
        var sequenced = await writer.SendAsync([WriteClient.Write(schema, new { name = "temperature" })], sequence: 10);
        await Assert.That(sequenced.GetProperty("results").GetArrayLength()).IsEqualTo(1);
        foreach (var sequence in new long[] { 10, 9 })
        {
            var duplicate = await writer.SendAsync([WriteClient.Write(schema, new { name = "temperature" })], sequence: sequence);
            await Assert.That(duplicate.GetProperty("duplicate").GetBoolean()).IsTrue();
            await Assert.That(duplicate.GetProperty("results").GetArrayLength()).IsEqualTo(0);
        }
    }

    [Test, Property("caseId", "api-writes")]
    public async Task WrongShapeOnAnExistingObservationAndLongKeysRejectOnlyThatWrite()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var fields = """
            {"place":{"type":"object","description":"Place","required":true,"nullable":false,"fields":{"name":{"type":"string","description":"Name","required":true,"nullable":false}}},
             "tags":{"type":"array","description":"Tags","required":true,"nullable":false,"items":{"type":"string","description":"Tag","nullable":false}}}
            """;
        var schema = await writer.DeclareAsync(fields: fields);
        var tags = new[] { "one" };
        var good = new { place = new { name = "here" }, tags };
        await writer.WriteAsync(schema, good, "existing");
        var longKey = new string('k', 2049);
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { place = "here", tags }, "existing"),
            WriteClient.Write(schema, new { place = new { name = "here" }, tags = new { one = 1 } }, "existing"),
            WriteClient.Write(schema, good, longKey),
            WriteClient.Write(schema, good, "after"),
        ])).GetProperty("results");
        await Assert.That(results[0].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-2");
        await Assert.That(results[1].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-2");
        await Assert.That(results[2].GetProperty("error").GetProperty("status").GetInt32()).IsEqualTo(400);
        await Assert.That(results[3].GetProperty("changed").GetBoolean()).IsTrue();
        var longest = new string('k', 2048 - "test".Length);
        await Assert.That((await writer.SendAsync([WriteClient.Write(schema, good, longest)])).GetProperty("results")[0].GetProperty("changed").GetBoolean()).IsTrue();
    }

    [Test, Property("caseId", "storage-transactions"), Property("caseId", "api-writes")]
    public async Task RejectionRollsBackOnlyItsWriteIncludingKeysInputsAndContent()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var keysBefore = await BinaryContentTests.Count(db.ConnectionString, "recognition_keys");
        var observationsBefore = await BinaryContentTests.Count(db.ConnectionString, "observations");
        var invalid = new { startAt = "2026-10-31T02:00:00Z", endAt = "2026-10-31T01:00:00Z", timeZone = (string?)null };
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { name = "before" }, "before"),
            WriteClient.Write(schema, new { name = "reject" }, "retry", time: invalid, inputs: [writer.Observer.ObservationId]),
            WriteClient.Write(schema, new { name = "after" }, "after", time: UnknownTime, inputs: [writer.Observer.ObservationId]),
            WriteClient.Write(Guid.CreateVersion7(), new { }),
            WriteClient.Write(schema, new { name = "nul\0" }),
            WriteClient.Write(schema, new { name = "last" })], sequence: 1)).GetProperty("results");
        await Assert.That(results[1].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("OBSERVATION-6");
        await Assert.That(results[3].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-3");
        await Assert.That(results[4].GetProperty("error").GetProperty("status").GetInt32()).IsEqualTo(400);
        await Assert.That(results[5].GetProperty("changed").GetBoolean()).IsTrue();
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "recognition_keys")).IsEqualTo(keysBefore + 2);
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "observations")).IsEqualTo(observationsBefore + 3);
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "input_sets")).IsEqualTo(1);
        var corrected = await writer.WriteAsync(schema, new { name = "reject" }, "retry", time: UnknownTime);
        await Assert.That(corrected.Replaces.Length).IsEqualTo(0);
        var duplicate = await writer.SendAsync([], sequence: 1);
        await Assert.That(duplicate.GetProperty("duplicate").GetBoolean()).IsTrue();
    }

    [Test, Property("caseId", "api-write-references")]
    public async Task ReferencesResolveInOrderAndCompareSavedIds()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var links = await writer.DeclareAsync("links", """
            {"targetId":{"type":"reference","target":"observation","description":"Target","required":true,"nullable":false},
             "optionalObservationId":{"type":"reference","target":"observation","description":"Optional","required":true,"nullable":true},
             "text":{"type":"string","description":"Literal","required":true,"nullable":false},
             "nested":{"type":"array","description":"Links","required":true,"nullable":false,"items":{"type":"object","description":"Link","nullable":false,"fields":{"targetId":{"type":"reference","target":"observation","description":"Target","required":true,"nullable":false}}}}}
            """);
        var target = await writer.WriteAsync(schema, new { name = "first" }, "target");
        object Content(object reference) => new { targetId = reference, optionalObservationId = Guid.CreateVersion7(), text = target.ObservationId.ToString(), nested = new[] { new { targetId = reference } } };
        var original = await writer.WriteAsync(links, Content(target.ObservationId), "link");
        var next = await writer.WriteAsync(schema, new { name = "second" }, "target");
        var same = await writer.WriteAsync(links, Content(target.ObservationId), "link");
        await Assert.That(same.ObservationId).IsEqualTo(original.ObservationId);
        var byKey = await writer.WriteAsync(links, Content(new { schemaId = schema, key = new { scope = (string?)null, scheme = "test", value = "target" } }));
        var fields = byKey.Content;
        await Assert.That(fields.GetProperty("targetId").GetGuid()).IsEqualTo(next.ObservationId);
        await Assert.That(fields.GetProperty("optionalObservationId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(fields.GetProperty("text").GetString()).IsEqualTo(target.ObservationId.ToString());
        using var deleted = await http.DeleteAsync($"/observations/{target.ObservationId}"); deleted.EnsureSuccessStatusCode();
        var rejected = (await writer.SendAsync([WriteClient.Write(links, Content(target.ObservationId))])).GetProperty("results")[0];
        await Assert.That(rejected.GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("REFERENCE-3");
        var ordered = await writer.SendAsync([
            WriteClient.Write(schema, new { name = "third" }, "another-target"),
            WriteClient.Write(links, Content(new { schemaId = schema, key = new { scope = (string?)null, scheme = "test", value = "another-target" } }))]);
        await Assert.That(ordered.GetProperty("results")[1].TryGetProperty("observation", out _)).IsTrue();
    }

    [Test, Property("caseId", "api-write-references"), Property("caseId", "storage-transactions")]
    public async Task RejectedWritesRollbackRecognizedThingsAndGlobalKeysIgnoreObserverAndRevision()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var type = await writer.DeclareThingAsync("objects");
        const string fields = """{"objectId":{"type":"reference","target":"thing","role":"about","description":"Object","required":true,"nullable":false},"name":{"type":"string","description":"Name","required":true,"nullable":false}}""";
        var schema = await writer.DeclareAsync(fields: fields);
        var reference = new { schemaId = type, key = new { scope = (string?)null, scheme = "native", value = "source" } };
        var rejected = await writer.SendAsync([WriteClient.Write(schema, new { objectId = reference })]);
        await Assert.That(rejected.GetProperty("results")[0].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-11");
        var things = await http.GetFromJsonAsync<JsonElement>("things?resourceName=objects");
        await Assert.That(things.GetProperty("things").GetArrayLength()).IsEqualTo(0);
        var first = await writer.WriteAsync(schema, new { objectId = reference, name = "same" }, "source");
        var second = new WriteClient(http); await second.InitializeAsync();
        await second.DeclareAsync(fields: fields.Replace("Name", "New description", StringComparison.Ordinal), schemaId: schema);
        var same = await second.WriteAsync(schema, new { objectId = first.Content.GetProperty("objectId").GetGuid(), name = "same" }, "source", formation: "stated");
        await Assert.That(same.ObservationId).IsEqualTo(first.ObservationId);
        var changed = (await second.SendAsync([WriteClient.Write(schema, new { objectId = reference, name = "changed" }, "source")])).GetProperty("results")[0];
        await Assert.That(changed.GetProperty("replaced")[0].GetGuid()).IsEqualTo(first.ObservationId);
        await Assert.That(changed.GetProperty("observation").GetProperty("observerId").GetGuid()).IsEqualTo(second.ObserverId);
    }

    [Test, Property("caseId", "api-reading"), Property("caseId", "api-deletion")]
    public async Task DeletionTombstonesRecognitionAndBatchReadPreservesOrderDuplicatesAndStatus()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync();
        var first = await writer.WriteAsync(schema, new { }, "key");
        using var deleted = await http.DeleteAsync($"/observations/{first.ObservationId}"); deleted.EnsureSuccessStatusCode();
        var skipped = await writer.SendAsync([WriteClient.Write(schema, new { }, "key")]);
        await Assert.That(skipped.GetProperty("results")[0].GetProperty("skipped").GetBoolean()).IsTrue();
        var recreated = await writer.WriteAsync(schema, new { }, "another-key");
        using var read = await http.PostAsJsonAsync("/observations/read", new { observationIds = new[] { recreated.ObservationId, Guid.CreateVersion7(), first.ObservationId, recreated.ObservationId } });
        var results = (await WriteClient.JsonAsync(read)).GetProperty("results").EnumerateArray().ToArray();
        await Assert.That(results.Select(r => r.GetProperty("status").GetString()).SequenceEqual(["found", "not_found", "deleted", "found"])).IsTrue();
        using var excessive = await http.PostAsJsonAsync("/observations/read", new { observationIds = Enumerable.Repeat(first.ObservationId, 257) });
        await Assert.That(excessive.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var old = await http.PostAsJsonAsync("/versions", new { versions = Array.Empty<object>() });
        await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
    [Test, Property("caseId", "api-lists")]
    public async Task ObservationPaginationBindsFiltersAndKeepsUnknownTimesLast()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync();
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { }, time: UnknownTime),
            WriteClient.Write(schema, new { }, time: new { startAt = "2026-10-31T01:00:00Z", endAt = (string?)null, timeZone = (string?)null }),
            WriteClient.Write(schema, new { }, time: new { startAt = (string?)null, endAt = "2026-10-31T02:00:00Z", timeZone = (string?)null })])).GetProperty("results");
        var observations = results.EnumerateArray().Select(r => r.GetProperty("observation").GetProperty("observationId").GetGuid()).ToArray();
        foreach (var order in new[] { "asc", "desc" })
        {
            var actual = new List<Guid>(); string? cursor = null;
            do
            {
                using var response = await http.GetAsync($"/observations?order={order}&limit=1&observerId={writer.ObserverId}&resourceName=items" + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor)));
                response.EnsureSuccessStatusCode(); var page = await WriteClient.JsonAsync(response);
                actual.AddRange(page.GetProperty("observations").EnumerateArray().Select(v => v.GetProperty("observationId").GetGuid()));
                cursor = page.GetProperty("next").GetString();
                if (cursor is not null)
                {
                    using var changed = await http.GetAsync("/observations?after=" + Uri.EscapeDataString(cursor));
                    await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                }
            } while (cursor is not null);
            await Assert.That(actual.SequenceEqual(order == "asc" ? [observations[1], observations[2], observations[0]] : new[] { observations[2], observations[1], observations[0] })).IsTrue();
        }
        using var half = await http.GetAsync("/observations?from=2026-10-31T00:00:00Z");
        await Assert.That(half.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var range = await http.GetAsync("/observations?from=2026-10-31T01:00:00Z&to=2026-10-31T02:00:00Z");
        await Assert.That((await WriteClient.JsonAsync(range)).GetProperty("observations").GetArrayLength()).IsEqualTo(1);
    }

    [Test, Property("caseId", "api-writes"), Property("caseId", "api-referrers")]
    public async Task InputsShareOneSetAndContentAndSchemaFailuresHaveDifferentBoundaries()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var evidence = await writer.WriteAsync(schema, new { name = "evidence" });
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { name = "one" }, time: UnknownTime, inputs: [writer.Observer.ObservationId, evidence.ObservationId]),
            WriteClient.Write(schema, new { name = "two" }, time: UnknownTime, inputs: [evidence.ObservationId, writer.Observer.ObservationId])])).GetProperty("results");
        var inputId = results[0].GetProperty("observation").GetProperty("inputSetId").GetGuid();
        await Assert.That(results[1].GetProperty("observation").GetProperty("inputSetId").GetGuid()).IsEqualTo(inputId);
        using var referrers = await http.GetAsync($"/observations/{evidence.ObservationId}/referrers?path=/inputSetId");
        var references = (await WriteClient.JsonAsync(referrers)).GetProperty("referrers");
        await Assert.That(references.GetArrayLength()).IsEqualTo(2);
        await Assert.That(references.EnumerateArray().All(item => item.GetProperty("observation").GetProperty("inputSetId").GetGuid() == inputId)).IsTrue();
        var undeclared = Guid.CreateVersion7();
        using var invalidSchema = await http.PostAsJsonAsync("/writes", new { observer = writer.ObserverReference, sequence = 20,
            schemas = new[] { new { schemaId = undeclared, content = new { displayName = "", description = "Invalid", resourceName = "invalid", fields = new { } } } },
            writes = new[] { WriteClient.Write(schema, new { name = "not saved" }) } });
        await Assert.That(invalidSchema.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var valid = await writer.SendAsync([WriteClient.Write(schema, new { name = "saved" })], sequence: 20);
        await Assert.That(valid.GetProperty("results").GetArrayLength()).IsEqualTo(1);
        using var malformed = new StringContent("{\"observer\":" + JsonSerializer.Serialize(writer.ObserverReference) + ",\"writes\":[{\"schemaId\":\"" + schema + "\",\"content\":{\"name\":\"first\",\"name\":\"second\"}}]}", System.Text.Encoding.UTF8, "application/json");
        using var duplicateMember = await http.PostAsync("/writes", malformed);
        await Assert.That(duplicateMember.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test, Property("caseId", "api-reading"), Property("caseId", "api-errors"), Property("caseId", "api-writes"), Property("caseId", "storage-transactions")]
    public async Task ConcurrentRecognitionPreservesOneChainAndHttpBoundariesRemainExplicit()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => writer.WriteAsync(schema, new { name = "one" }, "shared")));
        await Assert.That(concurrent.Select(v => v.ObservationId).Distinct().Count()).IsEqualTo(1);
        var first = concurrent[0];
        var second = await writer.WriteAsync(schema, new { name = "two" }, "shared");
        var third = await writer.WriteAsync(schema, new { name = "three" }, "shared", time: new { startAt = "2026-10-06T09:00:00+08:00", endAt = "2026-10-06T01:00:01.1234567Z", timeZone = "Asia/Shanghai" });
        using var chainResponse = await http.GetAsync("observations?resourceName=items&view=all");
        var chain = await WriteClient.JsonAsync(chainResponse);
        await Assert.That(chain.GetProperty("observations").EnumerateArray().Select(v => v.GetProperty("observationId").GetGuid()).ToHashSet().SetEquals([first.ObservationId, second.ObservationId, third.ObservationId])).IsTrue();
        using var readResponse = await http.GetAsync($"observations/{third.ObservationId}");
        var read = await WriteClient.JsonAsync(readResponse);
        await Assert.That(read.EnumerateObject().Count()).IsEqualTo(13);
        await Assert.That(read.GetProperty("recordedAt").GetString()!.EndsWith('Z')).IsTrue();
        var time = read;
        await Assert.That(time.GetProperty("startAt").GetString()).IsEqualTo("2026-10-06T01:00:00.000000Z");
        await Assert.That(time.GetProperty("endAt").GetString()).IsEqualTo("2026-10-06T01:00:01.123456Z");
        using var invalid = await http.PostAsJsonAsync("writes", new { observer = writer.ObserverReference, writes = Array.Empty<object>(), recordedAt = "invalid" });
        using var missing = await http.GetAsync("observations/" + Guid.CreateVersion7());
        using var usedSchema = await http.DeleteAsync("schemas/" + schema);
        foreach (var response in new[] { invalid, missing, usedSchema })
        {
            var problem = await WriteClient.JsonAsync(response);
            await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/problem+json");
            await Assert.That(problem.GetProperty("status").GetInt32()).IsEqualTo((int)response.StatusCode);
            await Assert.That(problem.GetProperty("title").GetString()).IsNotNull();
            await Assert.That(problem.GetProperty("detail").GetString()).IsNotNull();
        }
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(usedSchema.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using var largeBody = new StringContent(new string(' ', 10 * 1024 * 1024 + 1), System.Text.Encoding.UTF8, "application/json");
        using var oversized = await http.PostAsync("writes", largeBody);
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

}
