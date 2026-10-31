import { describe, expect, it } from "vitest";
import type { FieldDefinition } from "@/api/types";
import { inputSet, observation } from "@/test/fixtures";
import { outgoingReferences, referenceLabel } from "./references";

const thing: FieldDefinition = { description: "", type: "reference", target: "thing" };
const version: FieldDefinition = { description: "", type: "reference", target: "version" };

describe("outgoingReferences", () => {
  it("Observation 引用 Observer、它的描述和 InputSet", () => {
    const item = observation({ observerId: "o", observerVersionId: "d", inputSetVersionId: "i" });
    expect(outgoingReferences(item, undefined)).toEqual([
      { path: "observerId", target: "thing", id: "o" },
      { path: "observerVersionId", target: "version", id: "d" },
      { path: "inputSetVersionId", target: "version", id: "i" },
    ]);
  });

  it("业务字段只取 Schema 定义为 reference 的，包括数组项和嵌套字段，按目标分事物和版本", () => {
    const fields: Record<string, FieldDefinition> = {
      title: { description: "", type: "string" },
      author: thing,
      tags: { description: "", type: "array", items: thing },
      meta: { description: "", type: "object", fields: { source: version } },
    };
    const item = observation({
      observerId: "o",
      observerVersionId: null,
      content: { title: "x", author: "a", tags: ["t1", "t2"], meta: { source: "s1" } },
    });
    expect(outgoingReferences(item, fields).slice(1)).toEqual([
      { path: "author", target: "thing", id: "a" },
      { path: "tags[0]", target: "thing", id: "t1" },
      { path: "tags[1]", target: "thing", id: "t2" },
      { path: "meta.source", target: "version", id: "s1" },
    ]);
  });

  it("InputSet 引用每个输入", () => {
    expect(outgoingReferences(inputSet(["a", "b"]), undefined)).toEqual([
      { path: "members[0]", target: "version", id: "a" },
      { path: "members[1]", target: "version", id: "b" },
    ]);
  });
});

describe("referenceLabel", () => {
  it("去掉内容的根和 Id、VersionId 后缀", () => {
    expect(referenceLabel("/content/localApplicationId")).toBe("localApplication");
    expect(referenceLabel("/observerVersionId")).toBe("observer");
    expect(referenceLabel("observerId")).toBe("observer");
  });

  it("数组下标写成方括号，嵌套字段用点连接", () => {
    expect(referenceLabel("/content/items/0/targetVersionId")).toBe("items[0].target");
    expect(referenceLabel("members[2]")).toBe("members[2]");
    expect(referenceLabel("tagsId[]")).toBe("tags[]");
  });
});
