using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal static class ForegroundRegistration
{
    internal static readonly Guid ApplicationSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000002");
    internal static readonly Guid PeriodSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000001");
    internal static readonly SchemaDefinition[] Definitions = SchemaDefinition.Load(typeof(ForegroundRegistration).Assembly,
        "Orisumi.Observers.ForegroundState.schemas.json");
    internal static readonly ThingTypeDefinition[] ThingTypes = ThingTypeDefinition.Load(typeof(ForegroundRegistration).Assembly, "Orisumi.Observers.ForegroundState.thing-types.json");
    internal static async Task RegisterAsync(DataClient data, string? programVersion = null, CancellationToken token = default)
    {
        await data.UseAsync([.. Definitions, .. DeviceContext.Definitions], [.. ThingTypes, .. DeviceContext.ThingTypes], ObserverDescription.Local(programVersion ?? data.PackageVersion ?? typeof(ForegroundRegistration).Assembly.GetName().Version!.ToString()), token);
        if (data.Device is { } device) await data.WriteAllAsync(device.Writes(), token);
    }
}
