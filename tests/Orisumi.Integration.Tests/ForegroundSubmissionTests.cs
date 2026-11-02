using Orisumi.Core;
using Orisumi.Observer.Runtime.Forwarding;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ForegroundSubmissionTests
{
    [Test, Property("caseId", "foreground-submission")]
    public async Task ApplicationNamesOnlyChangeWhenTheObservedNameChanges()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var local = await ForegroundTestStore.CreateAsync(http);
        foreach (var name in new string?[] { null, "Code", "Code", "Visual Studio Code", null })
        {
            var reading = ForegroundTimelineTests.Reading(0) with { Name = name };
            var capture = ForegroundCapture.Create(local.Applications.Resolve(reading), reading);
            local.Store.Checkpoint(capture, null); local.Store.FinishActive();
            if ((await local.Observations()).Length == 1)
            {
                var unnamed = (await local.Data.ListAsync("things?resourceName=local-applications")).Single();
                await Assert.That(unnamed.GetProperty("name").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
                await Assert.That(unnamed.GetProperty("nameObservationId").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
            }
        }
        var app = (await local.Data.ListAsync("things?resourceName=local-applications")).Single();
        await Assert.That(app.GetProperty("name").GetString()).IsEqualTo("Visual Studio Code");
        var currentName = await local.Data.ObservationAsync(app.GetProperty("nameObservationId").GetGuid());
        var chain = await local.Data.GetAsync($"observations?resourceName=names&thingId={app.GetProperty("thingId").GetGuid()}&view=all");
        await Assert.That(chain.GetProperty("observations").GetArrayLength()).IsEqualTo(2);
        await Assert.That(currentName.GetProperty("formation").GetString()).IsEqualTo("direct");
        await Assert.That(currentName.GetProperty("startAt").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        await Assert.That((await local.Observations()).Length).IsEqualTo(5);
    }

    [Test, Property("caseId", "foreground-submission")]
    public async Task OngoingPeriodIsWrittenOnceWhenItEnds()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var local = await ForegroundTestStore.CreateAsync(http);
        var capture = local.Capture(); local.Store.Checkpoint(capture, null);
        var extended = capture.Through(ForegroundTimelineTests.Reading(1).ObservedAt);
        local.Store.Checkpoint(extended, null);
        await Assert.That((await local.Observations()).Length).IsEqualTo(0);
        local.Store.FinishActive(); local.Store.FinishActive();
        var saved = (await local.Observations()).Single();
        await Assert.That(saved.Replaces.Length).IsEqualTo(0);
        var observation = saved;
        await Assert.That(observation.StartAt).IsEqualTo(capture.StartAt);
        await Assert.That(observation.EndAt).IsEqualTo(extended.EndAt);
        await Assert.That(local.Diagnostics.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "foreground-submission")]
    public async Task OfflineCollectionContinuesAndAllPeriodsArriveAfterReconnection()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var fixture = await ForegroundTestStore.CreateAsync(http);
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        var connected = false;
        await using var queue = new WriteQueue(directory.Data, http, () => connected);
        await using var server = new ForwardingServer(); await server.StartAsync(CancellationToken.None);
        var id = Guid.CreateVersion7(); server.Add(id, queue);
        using var local = new HttpClient { BaseAddress = new(server.InstanceAddress(id)) };
        await using var data = new DataClient(local, http, fixture.Data.Observer);
        await ForegroundRegistration.RegisterAsync(data);
        var store = new ForegroundStore(data, fixture.Device, fixture.Diagnostics.Add);
        var first = fixture.Capture(); var second = fixture.Capture(1);
        store.Checkpoint(first, null); store.Checkpoint(second, first); store.FinishActive();
        await Assert.That(queue.Pending).IsEqualTo(3);
        await Assert.That((await fixture.Periods()).Length).IsEqualTo(0);
        connected = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (queue.Pending != 0) await Task.Delay(25, timeout.Token);
        await Assert.That(queue.Rejected).IsEqualTo(0);
        await Assert.That((await fixture.Periods()).Length).IsEqualTo(2);
    }

    [Test, Property("caseId", "foreground-application-period-boundaries")]
    public async Task EventsBetweenPollsArePreservedAndSleepClosesWithoutExtending()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var local = await ForegroundTestStore.CreateAsync(http);
        var collector = new ForegroundCollector(local.Applications, local.Store, TimeSpan.FromSeconds(1),
            () => ForegroundTimelineTests.Reading(2), TextWriter.Null);
        collector.Observe(ForegroundTimelineTests.Reading(0));
        foreach (var ms in new[] { 100, 200 }) collector.Observe(ForegroundTimelineTests.Reading(0) with {
            Name = ms.ToString(System.Globalization.CultureInfo.InvariantCulture), ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(ms),
            Elapsed = TimeSpan.FromMilliseconds(ms), Awake = TimeSpan.FromMilliseconds(ms) });
        await Assert.That((await local.Observations()).Length).IsEqualTo(2);
        collector.Sleep(); collector.Poll();
        await Assert.That((await local.Observations()).Length).IsEqualTo(3);
        collector.Wake(); collector.Stop();
        var observations = await local.Observations();
        await Assert.That(observations.Length).IsEqualTo(4);
        await Assert.That(observations[2].EndAt).IsEqualTo(ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200));
    }
}
