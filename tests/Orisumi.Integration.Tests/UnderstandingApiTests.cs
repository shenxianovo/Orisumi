using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class UnderstandingApiTests
{
    [Test, Property("caseId", "api-understanding")]
    public async Task DenialFollowsClaimsAcrossGenerationsAndWithdrawalRestoresThem()
    {
        await using var f = await Fixture.Create();
        var first = await f.Writer.WriteAsync(f.Schema, new { name = "claim" }, formation: "inferred");
        var denial = await f.Review(first.ObservationId, "deny");
        var next = await f.Writer.WriteAsync(f.Schema, new { name = "claim" }, formation: "inferred");
        await Assert.That((await f.Current()).Count).IsEqualTo(0);
        var withdrawal = await f.Review(denial.ObservationId, "deny");
        await Assert.That((await f.Current()).Count).IsEqualTo(2);
        await f.Review(withdrawal.ObservationId, "deny");
        await Assert.That((await f.Current()).Count).IsEqualTo(0);
        var stated = await f.Writer.WriteAsync(f.Schema, new { name = "claim" }, formation: "stated");
        await Assert.That((await f.Current()).Single().GetProperty("observationId").GetGuid()).IsEqualTo(stated.ObservationId);
        await Assert.That((await f.Model.ListAsync("observations?resourceName=items&view=all")).Count).IsEqualTo(3);
        await Assert.That((await f.Model.ObservationAsync(next.ObservationId)).GetProperty("content").GetProperty("name").GetString()).IsEqualTo("claim");
    }

    [Test, Property("caseId", "api-understanding")]
    public async Task PartialDenialAndDeniedBasisAreReturnedByTheBackend()
    {
        await using var f = await Fixture.Create();
        var evidence = await f.Writer.WriteAsync(f.Schema, new { name = "evidence" }, time: Time(0, 2));
        var conclusion = await f.Writer.WriteAsync(f.Schema, new { name = "conclusion" }, inputs: [evidence.ObservationId]);
        await f.Review(evidence.ObservationId, "deny", Time(1, 2));
        var current = await f.Current();
        var partial = current.Single(item => item.GetProperty("observationId").GetGuid() == evidence.ObservationId);
        await Assert.That(partial.GetProperty("deniedRanges").GetArrayLength()).IsEqualTo(1);
        await Assert.That(partial.GetProperty("deniedRanges")[0].GetProperty("startAt").GetDateTimeOffset()).IsEqualTo(At(1));
        await Assert.That(current.Single(item => item.GetProperty("observationId").GetGuid() == conclusion.ObservationId).GetProperty("basisDenied").GetBoolean()).IsTrue();
        await f.Review(evidence.ObservationId, "deny", Time(0, 1));
        await Assert.That((await f.Current()).Count).IsEqualTo(1);
        var all = await f.Model.ListAsync("observations?resourceName=items&view=all");
        await Assert.That(all.All(item => !item.TryGetProperty("deniedRanges", out _) && !item.TryGetProperty("basisDenied", out _))).IsTrue();
    }

    [Test, Property("caseId", "api-replacement")]
    public async Task ReplacingModelCannotHideStatedOrConfirmedObservationsAndDeniedReplacementDoesNotReviveOld()
    {
        await using var f = await Fixture.Create();
        var stated = await f.Writer.WriteAsync(f.Schema, new { name = "user" }, formation: "stated");
        var confirmed = await f.Writer.WriteAsync(f.Schema, new { name = "confirmed" }, formation: "inferred");
        await f.Review(confirmed.ObservationId, "confirm");
        var old = await f.Writer.WriteAsync(f.Schema, new { name = "old" }, formation: "inferred");
        var replacement = await f.Writer.WriteAsync(f.Schema, new { name = "replacement" }, formation: "inferred", replaces: [stated.ObservationId, confirmed.ObservationId, old.ObservationId]);
        await f.Review(replacement.ObservationId, "deny");
        var visible = (await f.Current()).Select(item => item.GetProperty("observationId").GetGuid()).ToHashSet();
        await Assert.That(visible.SetEquals([stated.ObservationId, confirmed.ObservationId])).IsTrue();
    }

    [Test, Property("caseId", "api-adoption")]
    public async Task AdoptionSelectsGenerationWithinItsRangeAndPreservesStatedAndConfirmed()
    {
        await using var f = await Fixture.Create();
        var first = await f.Writer.WriteAsync(f.Schema, new { name = "first" }, time: Time(0, 1), formation: "inferred");
        var outside = await f.Writer.WriteAsync(f.Schema, new { name = "outside" }, time: Time(3, 4), formation: "inferred");
        var confirmed = await f.Writer.WriteAsync(f.Schema, new { name = "confirmed" }, time: Time(0, 1), formation: "inferred");
        await f.Review(confirmed.ObservationId, "confirm");
        await f.Writer.SendAsync([], description: new { platform = "test", hostName = "test", programVersion = "2", model = "test/new", decisionModel = (string?)null });
        var next = await f.Writer.WriteAsync(f.Schema, new { name = "next" }, time: Time(0, 1), formation: "inferred");
        await f.Writer.WriteAsync(CommonSchemas.Adoption, new { schemaId = f.Schema, observerDescriptionId = next.ObserverDescriptionId }, time: Time(0, 2), formation: "stated");
        var visible = (await f.Current()).Select(item => item.GetProperty("observationId").GetGuid()).ToHashSet();
        await Assert.That(visible.SetEquals([outside.ObservationId, confirmed.ObservationId, next.ObservationId])).IsTrue();
        await f.Writer.WriteAsync(CommonSchemas.Adoption, new { schemaId = f.Schema, observerDescriptionId = first.ObserverDescriptionId }, time: Time(0, 2), formation: "stated");
        await Assert.That((await f.Current()).Any(item => item.GetProperty("observationId").GetGuid() == first.ObservationId)).IsTrue();
    }

    [Test, Property("caseId", "api-understanding")]
    public async Task ScopeDenialCoversLateObservationsAndStateSelectionFollowsTimeFiltering()
    {
        await using var f = await Fixture.Create();
        await f.Person.WriteAsync(new(CommonSchemas.Review, new { targetId = (Guid?)null, scope = new { thingIds = Array.Empty<Guid>(), schemaIds = new[] { f.Schema } }, verdict = "deny", text = (string?)null }, "stated", Time: new(At(0), At(2), null)));
        await f.Writer.WriteAsync(f.Schema, new { name = "late" }, time: Time(0, 1));
        await Assert.That((await f.Current()).Count).IsEqualTo(0);
        var stateSchema = await f.Writer.DeclareAsync("states", WriteApiTests.NameFields, stateFields: []);
        var earlier = await f.Writer.WriteAsync(stateSchema, new { name = "earlier" }, time: Time(0, 1));
        await f.Writer.WriteAsync(stateSchema, new { name = "later" }, time: Time(3, 4));
        var earlierPage = await f.Model.ListAsync("observations?resourceName=states&from=2026-10-01T00:00:00Z&to=2026-10-01T02:00:00Z");
        await Assert.That(earlierPage.Single().GetProperty("observationId").GetGuid()).IsEqualTo(earlier.ObservationId);
    }

    [Test, Property("caseId", "api-adoption")]
    public async Task PartiallyDeniedAdoptionOnlyFiltersTheRemainingTime()
    {
        await using var f = await Fixture.Create();
        var old = await f.Writer.WriteAsync(f.Schema, new { name = "old" }, time: Time(0, 1), formation: "inferred");
        await f.Writer.SendAsync([], description: new { platform = "test", hostName = "test", programVersion = "2", model = "test/new", decisionModel = (string?)null });
        var next = await f.Writer.WriteAsync(f.Schema, new { name = "new" }, time: Time(0, 1), formation: "inferred");
        var adoption = await f.Writer.WriteAsync(CommonSchemas.Adoption, new { schemaId = f.Schema, observerDescriptionId = next.ObserverDescriptionId }, time: Time(0, 2), formation: "stated");
        await f.Review(adoption.ObservationId, "deny", Time(0, 1));
        await Assert.That((await f.Current()).Select(item => item.GetProperty("observationId").GetGuid()).ToHashSet().SetEquals([old.ObservationId, next.ObservationId])).IsTrue();
    }

    internal static DateTimeOffset At(int hour) => new(2026, 10, 1, hour, 0, 0, TimeSpan.Zero);
    internal static object Time(int from, int to) => new { startAt = At(from), endAt = At(to), timeZone = (string?)null };
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDatabase db;
        private readonly OrisumiApiFactory factory;
        internal HttpClient Http { get; }
        internal WriteClient Writer { get; }
        internal DataClient Model { get; private set; } = null!;
        internal DataClient Person { get; }
        internal Guid Schema { get; private set; }
        private Fixture(TestDatabase database)
        {
            db = database; factory = new(db.ConnectionString); Http = factory.CreateClient(); Writer = new(Http);
            Person = new(Http, Http, Ref.Recognize(CommonSchemas.Person, new(null, "orisumi.user", "owner")));
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(await DatabaseFixture.CreateAsync()); await f.Writer.InitializeAsync();
            f.Model = new(f.Http, f.Http, Ref.Saved(f.Writer.ObserverId));
            f.Schema = await f.Writer.DeclareAsync(fields: WriteApiTests.NameFields, claimFields: ["name"]); return f;
        }
        internal Task<IReadOnlyList<JsonElement>> Current() => Model.ListAsync("observations?resourceName=items");
        internal async Task<Observation> Review(Guid target, string verdict, object? time = null)
        {
            var fact = time is null ? null : JsonSerializer.Deserialize<FactTime>(JsonSerializer.Serialize(time), DataClient.Json);
            var result = await Person.WriteAsync(new(CommonSchemas.Review, new { targetId = target, scope = (object?)null, verdict, text = (string?)null }, "stated", Time: fact));
            return ObservationJson.ReadSaved(result!.Observation!.Value);
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await factory.DisposeAsync(); await db.DisposeAsync(); }
    }
}
