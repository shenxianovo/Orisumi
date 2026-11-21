using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Collectors.Browser;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BrowserDatabase.Hello))]
[JsonSerializable(typeof(BrowserDatabase.Delivery))]
[JsonSerializable(typeof(BrowserDatabase.Reply))]
internal sealed partial class BrowserJson : JsonSerializerContext;
