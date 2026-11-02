using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRecognizer(DataClient data, IdentityRepository repository, IApplicationModel model,
    string modelName, string programVersion, Action<string> diagnostic, string decisionModelName = "gpt-6-luna")
{
    // Identifiers the model answered unknown for, with what it saw then. Kept only while the process runs.
    private readonly Dictionary<string, string> unknown = [];

    public async Task RunOnceAsync(CancellationToken token)
    {
        await data.UseAsync(IdentityData.Definitions, IdentityData.ThingTypes, ObserverDescription.Local(programVersion, "openai/" + modelName, "openai/" + decisionModelName), token);
        if (!model.IsConfigured) { diagnostic("OPENAI_API_KEY 未配置，本轮不识别。"); return; }
        var applications = await repository.ApplicationsAsync(token);
        if (applications.Length == 0) return;
        var identifiers = await repository.IdentifiersAsync(token);
        var groups = applications.SelectMany(app => IdentityData.Identifiers(app).Distinct()
            .Select(identifier => (identifier, app))).GroupBy(item => item.identifier);
        // Each decision sees the products and memberships saved by the previous decision.
        foreach (var group in groups)
        {
            try { await IdentifyAsync(group.Key, identifiers, group.Select(item => item.app).ToArray(), token); }
            catch (Exception exception) when (exception is WriteRejectedException or HttpRequestException) { diagnostic($"这条线索下轮重试：{exception.Message}"); }
        }
    }

    private async Task IdentifyAsync(Guid identifierId, Dictionary<Guid, ApplicationIdentifier> identifiers, Observation[] apps, CancellationToken token)
    {
        var identifier = identifiers[identifierId];
        var (products, memberships) = await repository.CatalogAsync(token);
        var key = JsonSerializer.Serialize(identifier, IdentityData.Json);
        if (memberships.Any(m => m.IdentifierId == identifierId && products.Any(p => p.ThingId == m.ProductId)))
        {
            unknown.Remove(key);
            return;
        }
        var periods = new List<Observation>();
        foreach (var app in apps) periods.AddRange(await repository.PeriodsAsync(IdentityData.Fields(app).GetProperty("localApplicationId").GetGuid(), token));
        var names = periods.Select(period => IdentityData.Fields(period).GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = JsonSerializer.Serialize(new { applications = apps.Select(app => app.ObservationId).Order().ToArray(), names });
        if (unknown.GetValueOrDefault(key) == fingerprint) return;
        var denials = (await repository.DenialsAsync(products, token)).Where(d => d.IdentifierId == identifierId).ToArray();
        IdentificationDecision decision;
        try
        {
            decision = await model.IdentifyAsync(new(identifier.Scheme.Split('.')[0], identifier,
                apps.Select(app => IdentityData.Identifiers(app).Select(id => identifiers[id]).Concat(
                    IdentityData.Fields(app).GetProperty("executablePath").GetString() is { } path
                        ? [new ApplicationIdentifier("macos.executable-path", path)] : []).ToArray()).ToArray(), names,
                products.Select(product => new ProductCandidate(product.ThingId, product.Name, memberships.Where(m => m.ProductId == product.ThingId).Select(m => identifiers[m.IdentifierId]).ToArray())).ToArray(), denials.Select(d => d.Conclusion).ToArray()), token);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
            || exception is OperationCanceledException && !token.IsCancellationRequested)
        { diagnostic($"模型判断失败，下轮重试：{exception.Message}"); return; }
        if (decision.Kind == "unknown" && decision.ProductId is null && decision.Name is null)
        {
            unknown[key] = fingerprint;
            return;
        }
        var target = decision.Kind == "existing" ? products.SingleOrDefault(product => product.ThingId == decision.ProductId) : null;
        if (decision.Kind is not ("existing" or "new") || decision.Kind == "existing" && target is null
            || decision.Kind == "existing" && decision.Name is not null
            || decision.Kind == "new" && (string.IsNullOrWhiteSpace(decision.Name) || decision.ProductId is not null))
        { diagnostic("模型回答无效，下轮重试。"); return; }
        var inputs = apps.Concat(periods.Where(p => !string.IsNullOrWhiteSpace(IdentityData.Fields(p).GetProperty("name").GetString())))
            .Concat(memberships.Select(m => m.Observation)).Concat(denials.Select(d => d.Observation))
            .Select(v => v.ObservationId).Distinct().ToArray();
        if (denials.Any(d => target is not null ? d.Conclusion.ProductId == target.ThingId
            : string.Equals(d.Conclusion.Name, decision.Name, StringComparison.Ordinal))) return;
        await SaveJudgmentAsync(identifierId, decision, inputs, [], token);
        unknown.Remove(key);
    }

    // Rejudgment persistence is independent of when a future caller asks to rejudge a clue.
    internal async Task SaveJudgmentAsync(Guid identifierId, IdentificationDecision decision, Guid[] inputs,
        IReadOnlyList<Observation> replaced, CancellationToken token)
    {
        object productRef = decision.ProductId is { } productId ? productId : Ref.Recognize(IdentityData.ProductSchemaId,
            new(data.Observer.Id!.Value.ToString("D"), "application-identity.product", decision.Name!));
        var writes = new List<WriteItem>();
        if (decision.Kind == "new") writes.Add(new(CommonSchemas.Name, new { thingId = productRef, name = decision.Name }, "inferred", Inputs: inputs));
        writes.Add(new(IdentityData.MembershipSchemaId, new MembershipContent(identifierId, productRef), "inferred", Inputs: inputs, Replaces: replaced.Select(item => item.ObservationId).ToArray()));
        await data.WriteAllAsync(writes, token);
    }
}
