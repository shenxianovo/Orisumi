using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Observer.Sdk;
using Orisumi.Core;
using Orisumi.Testing;
using Orisumi.Observers.ApplicationIdentity;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ApplicationIdentityTests
{
    [Test, Property("caseId", "application-identity")]
    public async Task SharedCluesHaveOneJudgmentAndNewProductAndNameShareOneRequest()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Application("com.qq", "/a", "QQ");
        var second = await f.Application("com.qq", "/b", "QQ");
        f.Model.Decide = _ => new("new", Name: "QQ");
        await f.Run();
        var products = await f.Data.ListAsync("things?resourceName=application-products");
        await Assert.That(products.Count).IsEqualTo(1);
        await Assert.That(products[0].GetProperty("name").GetString()).IsEqualTo("QQ");
        await Assert.That(f.Model.Inputs.Single().Applications.Length).IsEqualTo(2);
        var membership = (await f.Repository.CatalogAsync(default)).Memberships.Single().Observation;
        var observation = membership;
        await Assert.That(observation.Formation).IsEqualTo("inferred");
        var inputs = (await f.Data.GetAsync($"input-sets/{observation.InputSetId}")).Deserialize<InputSet>(DataClient.Json)!;
        await Assert.That(inputs.Members.Contains(first.ObservationId)).IsTrue();
        await Assert.That(inputs.Members.Contains(second.ObservationId)).IsTrue();
        await Assert.That(f.Network.Judgments.Single().GetProperty("writes").GetArrayLength()).IsEqualTo(2);
        await f.Run();
        await Assert.That(f.Model.Inputs.Count).IsEqualTo(1);
        await f.Application("android.qq", "/android", "QQ", "android.package-name");
        f.Model.Decide = _ => new("existing", products[0].GetProperty("thingId").GetGuid());
        await f.Run();
        await Assert.That((await f.Repository.CatalogAsync(default)).Memberships.Length).IsEqualTo(2);
        await Assert.That((await f.Data.ListAsync("things?resourceName=application-products")).Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task DenialReturnsClueToUnassignedAndRepeatedConclusionIsSkipped()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Application("com.qqmusic", "/music", "QQ Music");
        f.Model.Decide = _ => new("new", Name: "QQ");
        await f.Run();
        var judgment = (await f.Repository.CatalogAsync(default)).Memberships.Single();
        await using var person = new DataClient(f.Http, f.Http, Ref.Recognize(Guid.Parse("019e1bbb-ec00-7000-8000-000000000006"), new(null, "orisumi.user", "owner")));
        await person.WriteAsync(new(CommonSchemas.Review, new { targetId = judgment.Observation.ObservationId, scope = (object?)null, verdict = "deny", text = "不是 QQ" }, "stated"));
        f.Model.Decide = _ => new("existing", judgment.ProductId);
        await f.Run();
        await Assert.That(f.Model.Inputs[^1].Denied.Single().ProductId).IsEqualTo(judgment.ProductId);
        await Assert.That((await f.Repository.CatalogAsync(default)).Memberships.Length).IsEqualTo(0);
        f.Model.Decide = _ => new("new", Name: "QQ");
        await f.Run();
        await Assert.That((await f.Data.ListAsync("observations?resourceName=application-memberships&view=all")).Count).IsEqualTo(1);
        f.Model.Decide = _ => new("new", Name: "QQ Music");
        await f.Run();
        var next = (await f.Repository.CatalogAsync(default)).Memberships.Single().Observation;
        await Assert.That(next.ObservationId == judgment.Observation.ObservationId).IsFalse();
        await Assert.That(next.Replaces.Length).IsEqualTo(0);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task UnknownWaitsForNewEvidenceAndRestartClearsItsMemory()
    {
        await using var f = await Fixture.CreateAsync();
        var app = await f.Application("unknown", "/unknown", "Unknown");
        await f.Run(); await f.Run();
        await Assert.That(f.Model.Inputs.Count).IsEqualTo(1);
        await f.Period(app, "New name"); await f.Run();
        await Assert.That(f.Model.Inputs.Count).IsEqualTo(2);
        await f.Application("unknown", "/other", "Unknown"); await f.Run();
        await Assert.That(f.Model.Inputs.Count).IsEqualTo(3);
        f.Restart(); await f.Run();
        await Assert.That(f.Model.Inputs.Count).IsEqualTo(4);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task FailuresAndMissingKeyDoNotCreateJudgmentsAndKeepDescription()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Application("a", "/a", "A");
        f.Model.IsConfigured = false; await f.Run();
        await Assert.That(f.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(f.Model.Inputs.Count).IsEqualTo(0);
        var before = ObservationJson.ReadSaved((await f.Data.ListAsync("observations?resourceName=observer-descriptions")).Single(item => item.GetProperty("observerId").GetGuid() == f.Data.Observer.Id));
        f.Restart("other-model"); await f.Run();
        var after = ObservationJson.ReadSaved((await f.Data.ListAsync("observations?resourceName=observer-descriptions")).Single(item => item.GetProperty("observerId").GetGuid() == f.Data.Observer.Id));
        await Assert.That(after.Replaces.SingleOrDefault()).IsEqualTo(before.ObservationId);
        f.Model.IsConfigured = true;
        f.Model.Decide = _ => throw new TaskCanceledException("timeout"); await f.Run();
        f.Model.Decide = _ => new("existing", Guid.CreateVersion7()); await f.Run();
        f.Model.Decide = _ => new("new", Name: "A");
        f.Network.Reject = true; await f.Run();
        await Assert.That((await f.Data.ListAsync("things?resourceName=application-products")).Count).IsEqualTo(0);
        f.Network.Reject = false; await f.Run();
        await Assert.That((await f.Repository.CatalogAsync(default)).Memberships.Length).IsEqualTo(1);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task RejudgmentReplacesThePreviousJudgment()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Application("com.qq", "/qq", "QQ");
        f.Model.Decide = _ => new("new", Name: "QQ");
        await f.Run();
        var old = (await f.Repository.CatalogAsync(default)).Memberships.Single();
        f.Restart("new-model");
        await f.Run(); // Changing the model alone does not trigger rejudgment.
        await Assert.That(f.Model.Inputs.Count).IsEqualTo(1);
        await f.Rejudge(old);
        var current = (await f.Repository.CatalogAsync(default)).Memberships.Single();
        await Assert.That(current.Observation.ObservationId == old.Observation.ObservationId).IsFalse();
        await Assert.That(current.Observation.Replaces.Single()).IsEqualTo(old.Observation.ObservationId);
        await Assert.That(current.Observation.ObserverDescriptionId == old.Observation.ObserverDescriptionId).IsFalse();
        var all = await f.Data.ListAsync("observations?resourceName=application-memberships&view=all");
        await Assert.That(all.Count).IsEqualTo(2);

    }

    private sealed class FakeModel : IApplicationModel
    {
        public bool IsConfigured { get; set; } = true;
        public List<IdentificationInput> Inputs { get; } = [];
        public Func<IdentificationInput, IdentificationDecision> Decide { get; set; } = _ => new("unknown");
        public Task<IdentificationDecision> IdentifyAsync(IdentificationInput input, CancellationToken token)
        { Inputs.Add(input); return Task.FromResult(Decide(input)); }
    }
    private sealed class Network(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal bool Reject { get; set; }
        internal List<JsonElement> Judgments { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/writes")
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
                if (body.GetProperty("writes").EnumerateArray().Any(w => w.GetProperty("schemaId").GetGuid() == IdentityData.MembershipSchemaId))
                {
                    Judgments.Add(body);
                    if (Reject) return new(HttpStatusCode.ServiceUnavailable);
                }
            }
            return await base.SendAsync(request, token);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDatabase db;
        private readonly OrisumiApiFactory factory;
        internal HttpClient Http { get; }
        internal Network Network { get; }
        internal DataClient Data { get; private set; } = null!;
        internal IdentityRepository Repository => new(Data);
        internal FakeModel Model { get; } = new();
        internal List<string> Diagnostics { get; } = [];
        private WriteClient writer = null!;
        private IdentityRecognizer recognizer = null!;
        private readonly Guid description = Guid.Parse("019e1bbb-ec00-7000-8000-000000000014");
        private Fixture(TestDatabase database)
        { db = database; factory = new(db.ConnectionString); Network = new(factory.Server.CreateHandler()); Http = new(Network) { BaseAddress = new("http://localhost/") }; }
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await DatabaseFixture.CreateAsync());
            f.writer = new(f.Http); await f.writer.InitializeAsync();
            f.Data = new(f.Http, f.Http, Ref.Saved(f.writer.ObserverId));
            await f.writer.SendAsync([], ForegroundRegistration.Definitions.Select(d => (object)new { schemaId = d.SchemaId, content = d.Content }).ToArray(), thingTypes: ForegroundRegistration.ThingTypes.Cast<object>().ToArray());
            f.Restart(); return f;
        }
        internal async Task<Observation> Application(string value, string path, string name, string scheme = "macos.bundle-id")
        {
            var result = await writer.WriteAsync(description, new {
                localApplicationId = Ref.Recognize(ForegroundRegistration.ApplicationSchemaId, new(null, "test.installation", path)),
                deviceId = writer.ObserverId,
                identifierIds = new[] { Ref.Recognize(Guid.Parse("019e1bbb-ec00-7000-8000-000000000015"), new(null, scheme, value)) }, executablePath = path });
            // Existing thing references are strings, including a program instance used as a test device.
            await Period(result, name); return result;
        }
        internal Task<Observation> Period(Observation app, string name) => writer.WriteAsync(IdentityData.PeriodSchemaId,
            new { localApplicationId = IdentityData.Fields(app).GetProperty("localApplicationId").GetGuid(), name, icon = (object?)null });
        internal void Restart(string model = "test") => recognizer = new(Data, Repository, Model, model, "test", Diagnostics.Add);
        internal Task Rejudge(Membership previous) => recognizer.SaveJudgmentAsync(previous.IdentifierId,
            new("existing", previous.ProductId), [previous.Observation.ObservationId], [previous.Observation], default);
        internal Task Run() => recognizer.RunOnceAsync(default);
        public async ValueTask DisposeAsync() { Http.Dispose(); await factory.DisposeAsync(); await db.DisposeAsync(); }
    }
}
