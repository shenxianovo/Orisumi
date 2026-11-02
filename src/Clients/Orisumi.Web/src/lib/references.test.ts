import { describe, expect, it } from "vitest";
import type { FieldDefinition } from "@/api/types";
import { inputSet, observation as statement } from "@/test/fixtures";
import { outgoingReferences, referenceLabel } from "./references";

const thing: FieldDefinition = { description: "", type: "reference", target: "thing" };
const statementRef: FieldDefinition = { description: "", type: "reference", target: "observation" };

describe("outgoingReferences", () => {
  it("Observation 引用 Observer、说话人、它的描述和替换的说法", () => {
    const item = statement({ observerId: "o", speakerId: "s", observerDescriptionId: "d", replaces: ["r"] });
    expect(outgoingReferences(item, undefined)).toEqual([
      { path: "observerId", target: "thing", id: "o" },
      { path: "speakerId", target: "thing", id: "s" },
      { path: "observerDescriptionId", target: "observation", id: "d" },
      { path: "replaces[0]", target: "observation", id: "r" },
    ]);
  });

  it("业务字段只取 Schema 定义为 reference 的，包括数组项和嵌套字段，按目标分事物和 Observation", () => {
    const fields: Record<string, FieldDefinition> = {
      title: { description: "", type: "string" },
      author: thing,
      tags: { description: "", type: "array", items: thing },
      meta: { description: "", type: "object", fields: { source: statementRef } },
    };
    const item = statement({
      observerId: "o",
      observerDescriptionId: null,
      content: { title: "x", author: "a", tags: ["t1", "t2"], meta: { source: "s1" } },
    });
    expect(outgoingReferences(item, fields).slice(1)).toEqual([
      { path: "author", target: "thing", id: "a", role: undefined },
      { path: "tags[0]", target: "thing", id: "t1", role: undefined },
      { path: "tags[1]", target: "thing", id: "t2", role: undefined },
      { path: "meta.source", target: "observation", id: "s1", role: undefined },
    ]);
  });

  it("InputSet 引用每个输入", () => {
    expect(outgoingReferences(inputSet(["a", "b"]), undefined)).toEqual([
      { path: "members[0]", target: "observation", id: "a" },
      { path: "members[1]", target: "observation", id: "b" },
    ]);
  });
});

describe("referenceLabel", () => {
  it("去掉内容的根和 Id 后缀", () => {
    expect(referenceLabel("/content/localApplicationId")).toBe("localApplication");
    expect(referenceLabel("/observerDescriptionId")).toBe("observerDescription");
    expect(referenceLabel("observerId")).toBe("observer");
  });

  it("数组下标写成方括号，嵌套字段用点连接", () => {
    expect(referenceLabel("/content/items/0/targetId")).toBe("items[0].target");
    expect(referenceLabel("members[2]")).toBe("members[2]");
    expect(referenceLabel("tagsId[]")).toBe("tags[]");
  });
});
