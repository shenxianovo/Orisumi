using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Fixture;

public static class Program
{
    public static readonly Guid SchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-00000000fffe");
    public static readonly SchemaDefinition Definition = new(SchemaId, JsonSerializer.SerializeToElement(new
    {
        displayName = "Fixture Observer", description = "An HTTP SDK fixture.", resourceName = "fixture-observers",
        fields = new { name = new { type = "string", description = "Fixture name.", required = true, nullable = false } }
    }));
    public static async Task Main()
    {
        await using var data = DataClient.Connect();
        await data.UseAsync([Definition], [], ObserverDescription.Local("test"));
        try { await Task.Delay(Timeout.InfiniteTimeSpan, data.Stopping); }
        catch (OperationCanceledException) when (data.Stopping.IsCancellationRequested) { }
    }
    public static async Task<JsonElement> WriteAndReadAsync(DataClient data)
    {
        await data.UseAsync([Definition], [], ObserverDescription.Local("test"));
        var result = await data.Direct.WriteAsync(new(SchemaId, new { name = "sample" }, "direct"));
        return await data.ObservationAsync(result!.Observation!.Value.GetProperty("observationId").GetGuid());
    }
}
