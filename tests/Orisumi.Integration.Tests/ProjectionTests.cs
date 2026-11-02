using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Orisumi.Core;
using Orisumi.Infrastructure.Database;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class ProjectionTests
{
    [Test, Property("caseId", "storage-projections")]
    public async Task RebuildPreservesThingsReviewsHistoryAndDeletedTargets()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: WriteApiTests.NameFields);
        var first = await writer.WriteAsync(schema, new { name = "one" }, "key");
        var current = await writer.WriteAsync(schema, new { name = "two" }, "key");
        var review = await writer.WriteAsync(Guid.Parse("019e1bbb-ec00-7000-8000-00000000000e"),
            new { targetId = first.ObservationId, scope = (object?)null, verdict = "deny", text = (string?)null });
        using var deleted = await http.DeleteAsync($"/observations/{first.ObservationId}"); deleted.EnsureSuccessStatusCode();
        var paths = new[] { "observations", "things", "observations?view=current", "observations?view=all",
            $"observations/{first.ObservationId}/referrers?view=all", $"observations/{review.ObservationId}" };
        var before = new List<JsonElement>();
        foreach (var path in paths) before.Add(await http.GetFromJsonAsync<JsonElement>(path));
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresDataStore(source).RebuildProjectionsAsync();
        for (var i = 0; i < paths.Length; i++)
            await Assert.That(JsonElement.DeepEquals(before[i], await http.GetFromJsonAsync<JsonElement>(paths[i]))).IsTrue();
        var unchanged = await writer.WriteAsync(schema, new { name = "two" }, "key");
        await Assert.That(unchanged.ObservationId).IsEqualTo(current.ObservationId);
    }
}
