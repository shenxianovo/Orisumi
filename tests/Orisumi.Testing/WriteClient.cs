using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Testing;

public sealed class WriteClient(HttpClient http)
{
    private readonly string _observerKey = Guid.CreateVersion7().ToString();
    public Observation Observer { get; private set; } = null!;
    public Guid ObserverId => Observer.ObserverId;
    public object ObserverReference => new { schemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000012"), key = new { scope = (string?)null, scheme = "test", value = _observerKey } };
    public async Task InitializeAsync()
    {
        await SendAsync([], description: new { platform = "test", hostName = _observerKey, programVersion = "test", model = (string?)null, decisionModel = (string?)null });
        using var response = await http.GetAsync("observations?resourceName=observer-descriptions&limit=1000");
        var page = await JsonAsync(response);
        Observer = page.GetProperty("observations").EnumerateArray().Select(ObservationJson.ReadSaved)
            .Single(observation => observation.Content.GetProperty("hostName").GetString() == _observerKey);
    }
    public async Task<Guid> DeclareAsync(string resource = "items", string fields = "{}", Guid? schemaId = null, string[]? stateFields = null, string[]? claimFields = null)
    {
        var id = schemaId ?? Guid.CreateVersion7();
        var content = new Dictionary<string, object> { ["displayName"] = resource, ["description"] = "One test item.", ["resourceName"] = resource, ["fields"] = JsonSerializer.Deserialize<JsonElement>(fields) };
        if (stateFields is not null) content["stateFields"] = stateFields;
        if (claimFields is not null) content["claimFields"] = claimFields;
        await SendAsync([], [new { schemaId = id, content }]); return id;
    }
    public async Task<Guid> DeclareThingAsync(string resource, Guid? schemaId = null)
    {
        var id = schemaId ?? Guid.CreateVersion7();
        await SendAsync([], thingTypes: [new { schemaId = id, displayName = resource, description = "A test thing.", resourceName = resource }]); return id;
    }
    public async Task<JsonElement> SendAsync(object[] writes, object[]? schemas = null, long? sequence = null, object? description = null, object[]? thingTypes = null)
    {
        var body = new Dictionary<string, object?> { ["observer"] = ObserverReference, ["writes"] = writes };
        if (description is not null) body["description"] = description;
        if (schemas is not null) body["schemas"] = schemas;
        if (thingTypes is not null) body["thingTypes"] = thingTypes;
        if (sequence is not null) body["sequence"] = sequence;
        using var response = await http.PostAsJsonAsync("/writes", body);
        var json = await JsonAsync(response);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(json.GetRawText(), null, response.StatusCode);
        return json;
    }
    public async Task<Observation> WriteAsync(Guid schema, object content, string? key = null, object? time = null, Guid[]? inputs = null,
        string formation = "direct", Guid[]? replaces = null, Guid? runId = null)
    {
        var json = await SendAsync([Write(schema, content, key, time, inputs, formation, replaces, runId)]);
        var result = json.GetProperty("results")[0];
        if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetRawText());
        return ObservationJson.ReadSaved(result.GetProperty("observation"));
    }
    public static object Write(Guid schema, object content, string? key = null, object? time = null, Guid[]? inputs = null,
        string formation = "direct", Guid[]? replaces = null, Guid? runId = null)
    {
        var result = new Dictionary<string, object?> { ["schemaId"] = schema, ["content"] = content, ["formation"] = formation };
        if (key is not null) result["key"] = new { scope = (string?)null, scheme = "test", value = key };
        if (time is not null) result["time"] = time;
        if (inputs is not null) result["inputs"] = inputs;
        if (replaces is not null) result["replaces"] = replaces;
        if (runId is not null) result["runId"] = runId;
        return result;
    }
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
}
