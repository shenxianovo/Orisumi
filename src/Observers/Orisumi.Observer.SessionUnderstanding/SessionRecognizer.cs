using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.SessionUnderstanding;

internal sealed record SessionRelation(Guid ObjectId, string Predicate, string Text);
internal sealed record SessionDecision(string Name, SessionRelation[] Relations);
internal interface ISessionModel
{
    bool IsConfigured { get; }
    Task<SessionDecision> UnderstandAsync(object input, CancellationToken token);
}
internal sealed class SessionRecognizer(DataClient data, SessionRepository repository, ISessionModel model,
    string modelName, string programVersion, TimeSpan idle, TimeProvider time, Action<string> diagnostic)
{
    internal async Task RunOnceAsync(CancellationToken token)
    {
        await data.UseAsync([], [], ObserverDescription.Local(programVersion, "openai/" + modelName), token);
        if (!model.IsConfigured) { diagnostic("OPENAI_API_KEY 未配置，本轮不理解会话。"); return; }
        var sessions = await repository.SessionsAsync(token);
        for (var index = 0; index < sessions.Length; index++)
        {
            var session = sessions[index];
            if (session.Name is not null || session.Last is not { } last || last > time.GetUtcNow() - idle || session.Messages.Length == 0) continue;
            var denials = await repository.DenialsAsync(session.Id, token);
            try
            {
                var nearby = sessions.Where(s => s.Id != session.Id && s.Interactions.Any(v => SessionRecord.Time(v) is { } at && (at - last).Duration() <= TimeSpan.FromHours(24))).ToArray();
                var messages = session.Messages.Take(20).ToArray();
                var input = new { sessionId = session.Id, product = session.Product,
                    workingDirectory = SessionRecord.Fields(messages[0]).GetProperty("workingDirectory").GetString(),
                    messages = messages.Select(v => new { at = SessionRecord.Time(v), text = SessionRecord.Text(v) }),
                    nearby = nearby.Select(s => new { thingId = s.Id, product = s.Product, name = s.Name,
                        firstMessage = s.Messages.Length > 0 ? SessionRecord.Text(s.Messages[0]) : null }), denials };
                var decision = await model.UnderstandAsync(input, token);
                if (string.IsNullOrWhiteSpace(decision.Name) || decision.Name.EnumerateRunes().Count() > 16 || decision.Relations is null
                    || decision.Relations.Any(r => r is null || !nearby.Any(n => n.Id == r.ObjectId) || string.IsNullOrWhiteSpace(r.Predicate) || string.IsNullOrWhiteSpace(r.Text)))
                    throw new InvalidDataException("模型回答的名字或会话关系无效。");
                // A denied title leaves this session pending; do not accept it as a successful pass.
                if (denials.Any(d => d.Kind == "name" && d.Content.GetProperty("name").GetString() == decision.Name)) continue;
                var inputs = messages.Select(v => v.GetProperty("observationId").GetGuid())
                    .Concat(nearby.Where(s => s.NameObservationId.HasValue).Select(s => s.NameObservationId!.Value))
                    .Concat(nearby.Where(s => s.Messages.Length > 0).Select(s => s.Messages[0].GetProperty("observationId").GetGuid()))
                    .Concat(denials.SelectMany(d => new[] { d.ReviewObservationId, d.TargetObservationId })).Distinct().ToArray();
                var writes = new List<WriteItem> { new(CommonSchemas.Name, new { thingId = session.Id, name = decision.Name }, "inferred", Inputs: inputs) };
                writes.AddRange(decision.Relations.DistinctBy(r => (r.ObjectId, r.Predicate)).Where(r => !denials.Any(d => d.Kind == "relation"
                    && d.Content.GetProperty("objectId").GetGuid() == r.ObjectId && d.Content.GetProperty("predicate").GetString() == r.Predicate))
                    .Select(r => new WriteItem(CommonSchemas.Relation, new { subjectId = session.Id, objectId = r.ObjectId, predicate = r.Predicate, text = r.Text }, "inferred", Inputs: inputs)));
                var results = await data.WriteAllAsync(writes, token);
                sessions[index] = session with { Name = decision.Name, NameObservationId = results[0].Observation!.Value.GetProperty("observationId").GetGuid() };
            }
            catch (Exception e) when (e is HttpRequestException or WriteRejectedException or JsonException or InvalidDataException
                || e is OperationCanceledException && !token.IsCancellationRequested)
            { diagnostic($"会话 {session.Id} 下轮重试：{e.Message}"); }
        }
    }
}
