using System.Net;
using System.Text.Json;
using Orisumi.Observers.ApplicationIdentity;

namespace Orisumi.Integration.Tests;

public sealed class ApplicationModelTests
{
    [Test, Property("caseId", "application-identity")]
    [Arguments("new")]
    [Arguments("existing")]
    [Arguments("unknown")]
    public async Task DecisionsChoosesProductAndOnlyNewCallsResponses(string kind)
    {
        var id = Guid.CreateVersion7();
        var choice = kind == "existing" ? id.ToString() : kind;
        using var handler = new ResponseHandler(Choice(choice), """
            {"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{\"name\":\"QQ\"}"}]}]}
            """);
        using var http = new HttpClient(handler);
        var input = Input([new(id, "QQ", [])]);
        var result = await new OpenAiApplicationModel(http, "test-key", "test-model", "decision-model").IdentifyAsync(input, TestContext.Current!.Execution.CancellationToken);
        await Assert.That(result).IsEqualTo(kind == "existing" ? new(kind, id) : kind == "new" ? new(kind, Name: "QQ") : new IdentificationDecision(kind));
        await Assert.That(handler.Requests.Count).IsEqualTo(kind == "new" ? 2 : 1);
        await Assert.That(handler.Requests[0].Path).IsEqualTo("/v1/decisions");
        var body = handler.Requests[0].Body;
        await Assert.That(body.GetProperty("model").GetString()).IsEqualTo("decision-model");
        await Assert.That(body.GetProperty("questions")[0].GetProperty("type").GetString()).IsEqualTo("choice");
        if (kind == "new")
        {
            await Assert.That(handler.Requests[1].Path).IsEqualTo("/v1/responses");
            await Assert.That(handler.Requests[1].Body.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean()).IsTrue();
        }
    }

    [Test, Property("caseId", "application-identity")]
    public async Task CatalogIsBoundedAndDeniedProductsAreNotChoices()
    {
        var products = Enumerable.Range(0, 300).Select(i => new ProductCandidate(Guid.CreateVersion7(), "other" + i, [])).ToArray();
        var exact = new ProductCandidate(Guid.CreateVersion7(), "QQ", []);
        using var handler = new ResponseHandler(Choice("unknown"));
        using var http = new HttpClient(handler);
        await new OpenAiApplicationModel(http, "key", "model").IdentifyAsync(Input([.. products, exact]) with { Denied = [new(products[0].EntityId, products[0].Name, "wrong")] }, TestContext.Current!.Execution.CancellationToken);
        var choices = handler.Requests[0].Body.GetProperty("questions")[0].GetProperty("choices").EnumerateArray().Select(v => v.GetProperty("value").GetString()).ToArray();
        await Assert.That(choices.Length).IsEqualTo(255);
        await Assert.That(choices.Contains(exact.EntityId.ToString())).IsTrue();
        await Assert.That(choices.Contains(products[0].EntityId.ToString())).IsFalse();
    }

    [Test, Property("caseId", "application-identity")]
    [Arguments("{}")]
    [Arguments("{\"answers\":[{\"name\":\"product\",\"type\":\"refusal\"}]}")]
    [Arguments("{\"answers\":[{\"name\":\"product\",\"type\":\"choice\",\"probabilities\":[]}]}")]
    public async Task InvalidOrRefusedResponseIsADiagnosticFailure(string response)
    {
        using var handler = new ResponseHandler(response); using var http = new HttpClient(handler);
        await Assert.That(async () => await new OpenAiApplicationModel(http, "test-key", "test-model").IdentifyAsync(Input([]),
            TestContext.Current!.Execution.CancellationToken)).Throws<InvalidDataException>();
    }
    private static IdentificationInput Input(ProductCandidate[] products) => new("macos", new("macos.bundle-id", "com.tencent.qq"), [], ["QQ"], products, []);
    private static string Choice(string value) => JsonSerializer.Serialize(new { answers = new[] { new { type = "choice", name = "product", choice = value, probabilities = new[] { new { value, probability = 0.9 } } } } });
    private sealed class ResponseHandler(params string[] bodies) : HttpMessageHandler
    {
        internal List<(string Path, JsonElement Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken))));
            return new(HttpStatusCode.OK) { Content = new StringContent(bodies[Requests.Count - 1]) };
        }
    }
}
