using System.Text.Json;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.SessionUnderstanding;

namespace Orisumi.Integration.Tests;

public sealed class SessionUnderstandingTests
{
    [Test, Property("caseId", "assistant-sessions")]
    public async Task NamesIdleSessionsLinksNearbyAndKeepsEvidenceAndDenials()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("claude/projects/project/a.jsonl", AssistantTestFixture.ClaudeMessage("a", "继续另一个会话的设计", sessionId: "a"));
        fixture.Write("claude/projects/project/b.jsonl", AssistantTestFixture.ClaudeMessage("b", "实现会话理解", sessionId: "b"));
        await fixture.Collector.PollAsync();
        var repository = new SessionRepository(fixture.Data);
        var sessions = await repository.SessionsAsync(CancellationToken.None);
        var model = new FakeModel { Decide = input => {
            var json = JsonSerializer.SerializeToElement(input, JsonSerializerOptions.Web);
            var other = json.GetProperty("nearby")[0].GetProperty("entityId").GetGuid();
            return new("接续会话设计", [new(other, "继续了", "用户提到继续另一个会话")]);
        } };
        var recognizer = Recognizer(fixture, repository, model, DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await recognizer.RunOnceAsync(CancellationToken.None);
        var names = await fixture.Data.ListAsync("observations?resourceName=names&formation=inferred");
        var relations = await fixture.Data.ListAsync("observations?resourceName=relations");
        await Assert.That(names.Count).IsEqualTo(2); await Assert.That(relations.Count).IsEqualTo(2);
        await Assert.That(names.All(n => n.GetProperty("startAt").ValueKind == JsonValueKind.Null)).IsTrue();
        var secondInput = JsonSerializer.SerializeToElement(model.Inputs[1], JsonSerializerOptions.Web);
        await Assert.That(secondInput.GetProperty("nearby")[0].GetProperty("name").GetString()).IsEqualTo("接续会话设计");
        var inputSet = await fixture.Data.VersionAsync(names[0].GetProperty("inputSetVersionId").GetGuid());
        await Assert.That(inputSet.GetProperty("content").GetProperty("members").GetArrayLength() >= 2).IsTrue();
        await recognizer.RunOnceAsync(CancellationToken.None);
        await Assert.That(model.Inputs.Count).IsEqualTo(2);
        // A user denial makes the named session pending again and is supplied to the model.
        await using var person = new DataClient(fixture.Http, fixture.Http, Ref.Recognize(Guid.Parse("019e1bbb-ec00-7000-8000-000000000006"), new(null, "test.person", "user")));
        await person.WriteAsync(new(CommonSchemas.Review, new { targetVersionId = names[0].GetProperty("versionId").GetGuid(), verdict = "deny", text = "不是这个名字" }, "stated"));
        await recognizer.RunOnceAsync(CancellationToken.None);
        await Assert.That(model.Inputs.Count).IsEqualTo(3);
        await Assert.That((await fixture.Data.ListAsync("observations?resourceName=names&formation=inferred")).Count).IsEqualTo(2);
        var retry = JsonSerializer.SerializeToElement(model.Inputs[2], JsonSerializerOptions.Web);
        await Assert.That(retry.GetProperty("denials").GetArrayLength()).IsEqualTo(1);
        model.Decide = _ => new("重新审视设计", []);
        await recognizer.RunOnceAsync(CancellationToken.None);
        await Assert.That((await fixture.Data.ListAsync("observations?resourceName=names&formation=inferred&standing=current")).Count).IsEqualTo(2);
    }

    [Test, Property("caseId", "assistant-sessions")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecentOrUserlessSessionsAreSkippedAndInvalidRelationsRetry(bool nullRelation)
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("claude/projects/project/a.jsonl", AssistantTestFixture.ClaudeMessage("a", "给会话命名"));
        fixture.Write("claude/projects/project/b.jsonl", AssistantTestFixture.ClaudeMessage("b", "助手回复", "assistant", sessionId: "no-user"));
        await fixture.Collector.PollAsync();
        var model = new FakeModel { Decide = _ => new("命名会话", nullRelation ? [null!] : [new(Guid.CreateVersion7(), "继续了", "无证据")]) };
        var repository = new SessionRepository(fixture.Data);
        await Recognizer(fixture, repository, model, DateTimeOffset.Parse("2026-10-30T10:10:00Z", System.Globalization.CultureInfo.InvariantCulture)).RunOnceAsync(CancellationToken.None);
        await Assert.That(model.Inputs.Count).IsEqualTo(0);
        var recognizer = Recognizer(fixture, repository, model, DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await recognizer.RunOnceAsync(CancellationToken.None);
        await Assert.That((await fixture.Data.ListAsync("observations?resourceName=names&formation=inferred")).Count).IsEqualTo(0);
        model.Decide = _ => new("命名会话", []);
        await recognizer.RunOnceAsync(CancellationToken.None);
        await Assert.That(model.Inputs.Count).IsEqualTo(2);
        await Assert.That((await fixture.Data.ListAsync("observations?resourceName=names&formation=inferred")).Count).IsEqualTo(1);
    }

    private static SessionRecognizer Recognizer(AssistantTestFixture fixture, SessionRepository repository, FakeModel model, DateTimeOffset now) =>
        new(new DataClient(fixture.Http, fixture.Http, Ref.Recognize(CommonSchemas.Program, new(null, "test.session-understanding", "self"))), repository, model, "test-model", "1.0", TimeSpan.FromMinutes(30), new Clock(now), fixture.Diagnostics.Add);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class FakeModel : ISessionModel
    {
        public bool IsConfigured => true;
        internal Func<object, SessionDecision> Decide { get; set; } = _ => new("测试会话", []);
        internal List<object> Inputs { get; } = [];
        public Task<SessionDecision> UnderstandAsync(object input, CancellationToken token) { Inputs.Add(input); return Task.FromResult(Decide(input)); }
    }
}
