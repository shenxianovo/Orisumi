using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Model.Tests;

internal static class ModelFixture
{
    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    internal static Schema Schema(string fields = "{}") => new("Items", "An item", "items", Json(fields));
    internal static Observation Content(JsonElement fields) => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, null,
        "direct", null, [], null, null, null, DateTimeOffset.UtcNow, fields.Clone());
    internal static async Task Reject(Action action, string rule)
    {
        try { action(); }
        catch (ModelViolationException error) { await Assert.That(error.Rule).IsEqualTo(rule); return; }
        throw new InvalidOperationException($"Expected {rule}.");
    }
}
