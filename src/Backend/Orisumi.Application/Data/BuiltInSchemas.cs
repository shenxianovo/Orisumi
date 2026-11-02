using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Application.Data;

public static class BuiltInSchemas
{
    public static readonly Guid Person = Id("06"), Program = Id("12"), Review = Id("0e"), Relation = Id("0f"), Name = Id("10"),
        SameAs = Id("1a"), Adoption = Id("1b"), ObserverDescription = Id("1c"), Model = Id("1d");
    public static readonly IReadOnlyList<ThingType> ThingTypes =
    [
        new(Person, "人", "表示一个人。", "people"),
        new(Program, "程序实例", "表示一个对观测负责的程序实例或 Agent。", "program-instances"),
        new(Model, "模型", "表示一个说话的语言模型，用来源给出的模型名作识别依据。", "models"),
    ];
    public static readonly IReadOnlyList<SchemaDeclaration> All =
    [
        new(Review, Define("复核", "表示对一条陈述或一个范围的确认或否定。事实时间是它针对的时段，未知时针对整条。", "reviews",
            new { verdict = Text("confirm 确认或 deny 否定"), targetId = Reference("observation", "被复核的 Observation", nullable: true),
                scope = new { type = "object", description = "被复核的范围", required = true, nullable = true,
                    fields = new { thingIds = new { type = "array", description = "范围涉及的事物，空数组表示不限", required = true, nullable = false,
                            items = new { type = "reference", target = "thing", role = "mention", description = "范围涉及的事物", nullable = false } },
                        schemaIds = new { type = "array", description = "范围涉及的业务类型，空数组表示不限", required = true, nullable = false,
                            items = new { type = "string", description = "SchemaId", nullable = false } } } }, text = Text("复核的说明", true, true) })),
        new(Relation, Define("关系", "表示两个事物之间的一种关系。事实时间是这个关系成立的时段。", "relations",
            new { subjectId = Reference("thing", "关系的主语", "about"), predicate = Text("关系的说法", searchable: true),
                objectId = Reference("thing", "关系的宾语", "about"), text = Text("补充说明", true, true) }, claim: ["subjectId", "predicate", "objectId"])),
        new(Name, Define("名字", "表示一个事物的名字。事实时间是这个名字成立的时段，未知时为 null。", "names",
            new { thingId = Reference("thing", "被命名的事物", "about"), name = Text("名字的原文", searchable: true) }, ["thingId"], ["thingId", "name"])),
        new(SameAs, Define("同一", "表示两个事物是同一个。事实时间是成立的时段，未知时为 null。", "same-as",
            new { leftId = Reference("thing", "其中一个事物", "about"), rightId = Reference("thing", "另一个事物", "about") }, claim: ["leftId", "rightId"])),
        new(Adoption, Define("采用", "表示一种业务类型的模型判断采用哪一份 Observer 描述。事实时间是适用的时段，未知时为全部时间。", "adoptions",
            new { schemaId = Text("适用的业务类型"), observerDescriptionId = Reference("observation", "采用的那一代") })),
        new(ObserverDescription, Define("Observer 描述", "描述一个程序实例或 Agent：运行的平台、主机名、程序版本和调用的模型。事实时间未知。", "observer-descriptions",
            new { platform = Text("运行的平台"), hostName = Text("本机的名称，读不到时为 null", true), programVersion = Text("安装包的版本"),
                model = Text("调用的供应商和型号，不调用时为 null", true), decisionModel = Text("选择模型，没有这一步时为 null", true) })),
    ];
    private static Guid Id(string suffix) => Guid.Parse("019e1bbb-ec00-7000-8000-0000000000" + suffix);
    private static Schema Define(string name, string description, string resource, object fields, string[]? state = null, string[]? claim = null) =>
        new(name, description, resource, JsonSerializer.SerializeToElement(fields), state, claim);
    private static object Text(string description, bool nullable = false, bool searchable = false) => new { type = "string", description, required = true, nullable, searchable };
    private static Dictionary<string, object> Reference(string target, string description, string? role = null, bool nullable = false)
    {
        var field = new Dictionary<string, object> { ["type"] = "reference", ["target"] = target, ["description"] = description, ["required"] = true, ["nullable"] = nullable };
        if (role is not null) field["role"] = role;
        return field;
    }
}
