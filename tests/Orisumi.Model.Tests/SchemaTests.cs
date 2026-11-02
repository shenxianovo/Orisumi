using Orisumi.Core;
using static Orisumi.Model.Tests.ModelFixture;

namespace Orisumi.Model.Tests;

public sealed class SchemaTests
{
    // SCHEMA-4: only nonempty names and descriptions are accepted; prose meaning is the writer's responsibility.
    [Test]
    [Arguments("", "Description")]
    [Arguments("Name", "")]
    [Arguments(" ", "Description")]
    [Arguments("Name", "\t")]
    [Arguments(null, "Description")]
    [Arguments("Name", null)]
    public async Task EmptyNamesAndDescriptionsReportSchemaMetadataViolation(string? displayName, string? description)
    {
        await Reject(() => ObservationRules.ValidateSchema(new(displayName!, description!, "items", Json("{}"))), "SCHEMA-4");
    }

    // SCHEMA-7, SCHEMA-10, SCHEMA-12, SCHEMA-14
    [Test]
    [Arguments("{\"id\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false}}", "SCHEMA-7")]
    [Arguments("{\"x\":{\"description\":\"Test field value.\",\"required\":true,\"nullable\":false}}", "SCHEMA-10")]
    [Arguments("{\"x\":{\"type\":\"string\",\"description\":\"Test field value.\",\"nullable\":false}}", "SCHEMA-10")]
    [Arguments("{\"x\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true}}", "SCHEMA-10")]
    [Arguments("{\"x\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false,\"default\":\"x\"}}", "SCHEMA-12")]
    [Arguments("{\"x\":{\"type\":\"object\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false}}", "SCHEMA-14")]
    [Arguments("{\"x\":{\"type\":\"array\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false}}", "SCHEMA-14")]
    [Arguments("{\"x\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false,\"fields\":{}}}", "SCHEMA-14")]
    [Arguments("{\"x\":{\"type\":\"array\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false,\"items\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":false,\"nullable\":false}}}", "SCHEMA-10")]
    public async Task MalformedDeclarationsAreRejectedBeforeSaving(string fields, string rule)
    {
        await Reject(() => ObservationRules.ValidateSchema(Schema(fields)), rule);
    }


}
