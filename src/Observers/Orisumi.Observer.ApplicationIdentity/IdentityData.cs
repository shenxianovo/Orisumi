using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed record ApplicationIdentifier(string Scheme, string Value);
internal sealed record Product(Guid ThingId, string? Name);
internal sealed record Membership(Observation Observation, Guid IdentifierId, Guid ProductId);
internal sealed record DeniedConclusion(Guid ProductId, string? Name, string? Text);
internal sealed record Denial(Observation Observation, Guid IdentifierId, DeniedConclusion Conclusion);
internal sealed record MembershipContent(Guid IdentifierId, object ProductId);
internal sealed record IdentificationInput(string Platform, ApplicationIdentifier Identifier,
    ApplicationIdentifier[][] Applications, string[] Names, ProductCandidate[] Products, DeniedConclusion[] Denied);
internal sealed record ProductCandidate(Guid ThingId, string? Name, ApplicationIdentifier[] Identifiers);
internal sealed record IdentificationDecision(string Kind, Guid? ProductId = null, string? Name = null);

internal interface IApplicationModel
{
    bool IsConfigured { get; }
    Task<IdentificationDecision> IdentifyAsync(IdentificationInput input, CancellationToken token);
}

internal static class IdentityData
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly Guid ProductSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000004");
    internal static readonly Guid PeriodSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000001");
    internal static readonly Guid MembershipSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-00000000000c");
    internal static JsonElement Fields(Observation version) => version.Content;
    internal static Guid[] Identifiers(Observation version) => Fields(version).GetProperty("identifierIds").EnumerateArray().Select(v => v.GetGuid()).ToArray();
    internal static readonly ThingTypeDefinition[] ThingTypes = ThingTypeDefinition.Load(typeof(IdentityData).Assembly, "Orisumi.Observers.ApplicationIdentity.thing-types.json");
    internal static readonly SchemaDefinition[] Definitions = SchemaDefinition.Load(typeof(IdentityData).Assembly,
        "Orisumi.Observer.ApplicationIdentity.schemas.json");
}
