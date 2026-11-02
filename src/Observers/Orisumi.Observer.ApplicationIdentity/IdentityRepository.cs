using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRepository(DataClient data)
{
    internal async Task<Observation[]> ApplicationsAsync(CancellationToken token)
    {
        try { return (await data.ListAsync("observations?resourceName=local-application-descriptions&limit=1000", token)).Select(ObservationJson.ReadSaved).ToArray(); }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return []; }
    }

    internal async Task<Dictionary<Guid, ApplicationIdentifier>> IdentifiersAsync(CancellationToken token) =>
        (await data.ListAsync("things?resourceName=application-identifiers&limit=1000", token)).ToDictionary(
            v => v.GetProperty("thingId").GetGuid(), v => v.GetProperty("keys")[0].Deserialize<ApplicationIdentifier>(IdentityData.Json)!);

    internal async Task<(Product[] Products, Membership[] Memberships)> CatalogAsync(CancellationToken token)
    {
        var products = (await data.ListAsync("things?resourceName=application-products&limit=1000", token))
            .Select(v => new Product(v.GetProperty("thingId").GetGuid(), v.GetProperty("name").GetString())).ToArray();
        var memberships = (await data.ListAsync("observations?resourceName=application-memberships&view=current&limit=1000", token))
            .Select(ObservationJson.ReadSaved).Select(v => new Membership(v, IdentityData.Fields(v).GetProperty("identifierId").GetGuid(),
                IdentityData.Fields(v).GetProperty("productId").GetGuid())).ToArray();
        return (products, memberships);
    }

    internal async Task<Denial[]> DenialsAsync(Product[] products, CancellationToken token)
    {
        var reviews = (await data.ListAsync("observations?resourceName=reviews&limit=1000", token)).Select(ObservationJson.ReadSaved)
            .Where(v => v.Formation == "stated" && IdentityData.Fields(v).GetProperty("targetId").ValueKind == JsonValueKind.String && IdentityData.Fields(v).GetProperty("verdict").GetString() == "deny").ToArray();
        var targets = new Dictionary<Guid, JsonElement>();
        foreach (var chunk in reviews.Select(v => IdentityData.Fields(v).GetProperty("targetId").GetGuid()).Distinct().Chunk(256))
        {
            var page = await data.ReadObservationsAsync(chunk, token);
            foreach (var result in page.GetProperty("results").EnumerateArray())
                if (result.GetProperty("status").GetString() == "found") targets[result.GetProperty("observationId").GetGuid()] = result.GetProperty("observation");
        }
        var schemaIds = (await data.ListAsync("schema-revisions?resourceName=application-memberships", token)).Select(v => v.GetProperty("schemaRevisionId").GetGuid()).ToHashSet();
        var denials = new List<Denial>();
        foreach (var review in reviews)
        {
            var fields = IdentityData.Fields(review);
            if (!targets.TryGetValue(fields.GetProperty("targetId").GetGuid(), out var target)
                || !target.TryGetProperty("schemaRevisionId", out var schema) || !schemaIds.Contains(schema.GetGuid())) continue;
            var content = target.GetProperty("content");
            var product = content.GetProperty("productId").GetGuid();
            denials.Add(new(review, content.GetProperty("identifierId").GetGuid(), new(product, products.SingleOrDefault(p => p.ThingId == product)?.Name, fields.GetProperty("text").GetString())));
        }
        return denials.ToArray();
    }

    internal async Task<Observation[]> PeriodsAsync(Guid application, CancellationToken token)
    {
        var page = await data.GetAsync($"things/{application}/referrers?resourceName=foreground-application-periods&path=%2Fcontent%2FlocalApplicationId&limit=100", token);
        return page.GetProperty("referrers").EnumerateArray().Select(item => ObservationJson.ReadSaved(item.GetProperty("observation"))).ToArray();
    }
}
