using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class ReferrerApiTests
{
    [Test, Property("caseId", "api-referrers")]
    public async Task ReferencesFilterBeforePagingKeepPathsAndSurviveTargetDeletion()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var items = await writer.DeclareAsync(fields: WriteApiTests.NameFields);
        var schema = await writer.DeclareAsync("links", """
            {"a~/ObservationId":{"type":"reference","target":"observation","description":"Target","required":true,"nullable":false},
             "otherObservationId":{"type":"reference","target":"observation","description":"Other","required":true,"nullable":false}}
            """);
        var target = await writer.WriteAsync(items, new { name = "target" });
        var other = await writer.WriteAsync(items, new { name = "other" });
        object Content(Guid a, Guid b) => new Dictionary<string, Guid> { ["a~/ObservationId"] = a, ["otherObservationId"] = b };
        var first = await writer.WriteAsync(schema, Content(target.ObservationId, target.ObservationId), "link");
        var current = await writer.WriteAsync(schema, Content(other.ObservationId, target.ObservationId), "link");
        var third = await writer.WriteAsync(schema, Content(target.ObservationId, other.ObservationId));
        var url = $"/observations/{target.ObservationId}/referrers";
        using var all = await http.GetAsync(url + "?view=all");
        var entries = (await WriteClient.JsonAsync(all)).GetProperty("referrers");
        await Assert.That(entries.GetArrayLength()).IsEqualTo(3);
        await Assert.That(entries[0].GetProperty("references").GetArrayLength()).IsEqualTo(2);
        using var filtered = await http.GetAsync(url + "?view=all&resourceName=links&path=/content/a~0~1ObservationId&limit=1");
        var page = await WriteClient.JsonAsync(filtered);
        await Assert.That(page.GetProperty("referrers")[0].GetProperty("observation").GetProperty("observationId").GetGuid()).IsEqualTo(first.ObservationId);
        var cursor = Uri.EscapeDataString(page.GetProperty("next").GetString()!);
        using var next = await http.GetAsync(url + "?view=all&resourceName=links&path=/content/a~0~1ObservationId&limit=1&after=" + cursor);
        await Assert.That((await WriteClient.JsonAsync(next)).GetProperty("referrers")[0].GetProperty("observation").GetProperty("observationId").GetGuid()).IsEqualTo(third.ObservationId);
        using var changedQuery = await http.GetAsync(url + "?view=all&resourceName=links&after=" + cursor);
        await Assert.That(changedQuery.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var descending = await http.GetAsync(url + "?order=desc");
        await Assert.That((await WriteClient.JsonAsync(descending)).GetProperty("referrers").EnumerateArray().Select(e => e.GetProperty("observation").GetProperty("observationId").GetGuid()).SequenceEqual([third.ObservationId, current.ObservationId])).IsTrue();
        using var deleted = await http.DeleteAsync($"/observations/{target.ObservationId}"); deleted.EnsureSuccessStatusCode();
        using var retained = await http.GetAsync(url + "?view=all");
        await Assert.That(JsonElement.DeepEquals(entries, (await WriteClient.JsonAsync(retained)).GetProperty("referrers"))).IsTrue();
        using var removed = await http.DeleteAsync($"/observations/{third.ObservationId}"); removed.EnsureSuccessStatusCode();
        using var after = await http.GetAsync(url);
        await Assert.That((await WriteClient.JsonAsync(after)).GetProperty("referrers").GetArrayLength()).IsEqualTo(1);
        using var unknown = await http.GetAsync($"/observations/{Guid.CreateVersion7()}/referrers");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
