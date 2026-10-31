import { describe, expect, it } from "vitest";
import type { Version } from "@/api/types";
import { observation } from "@/test/fixtures";
import { changedFields, describeChanges, versionLabel, versionNumber } from "./versions";

function version(versionId: string, content: Record<string, unknown>): Version {
  return observation({ entityId: "e", versionId, content });
}

describe("versionNumber / versionLabel", () => {
  const chain = [version("a", {}), version("b", {}), version("c", {})];

  it("序号从 1 开始，不在版本链中时为 null", () => {
    expect(versionNumber(chain, "b")).toBe(2);
    expect(versionNumber(chain, "x")).toBeNull();
  });

  it("已有更新的版本时写出当前版本号", () => {
    expect(versionLabel(9, 12)).toBe("v9 / v12");
    expect(versionLabel(12, 12)).toBe("v12");
  });
});

describe("changedFields", () => {
  it("列出相对前一版变化、新增和移除的字段", () => {
    const before = version("a", { endAt: "1", title: "x", gone: 1 });
    const after = version("b", { endAt: "2", title: "x", added: true });
    expect(changedFields(before, after)).toEqual(["endAt", "added", "gone"]);
  });

  it("首版没有前一版", () => {
    expect(changedFields(undefined, version("a", { x: 1 }))).toEqual([]);
  });
});

describe("describeChanges", () => {
  it("数组字段写加入和移出的元素，对象元素写出它的标量值", () => {
    const clue = (value: string) => ({ value, scheme: "macos.bundle-id" });
    const before = version("a", { name: "Preview", identifiers: [clue("com.apple.Old")] });
    const after = version("b", { name: "Preview", identifiers: [clue("com.apple.Preview")] });
    expect(describeChanges(before, after, new Map())).toEqual([
      "加入 macos.bundle-id com.apple.Preview",
      "移出 macos.bundle-id com.apple.Old",
    ]);
  });

  it("标量写新的值，对象写改了，去掉的字段写去掉", () => {
    const before = version("a", { name: "Preview", icon: { digest: "x" }, gone: 1 });
    const after = version("b", { name: "Apple Preview", icon: { digest: "y" } });
    expect(describeChanges(before, after, new Map())).toEqual(["name 改为 Apple Preview", "icon 改了", "去掉 gone"]);
  });

  it("没有前一版时是新建和数组里的元素，没有变化时是内容相同", () => {
    const first = version("a", { name: "QQ", identifiers: [{ scheme: "macos.bundle-id", value: "com.tencent.qq" }] });
    expect(describeChanges(null, first, new Map())).toEqual(["新建", "加入 macos.bundle-id com.tencent.qq"]);
    expect(describeChanges(version("a", { x: 1 }), version("b", { x: 1 }), new Map())).toEqual(["内容相同"]);
  });

  it("引用写目标的名字，读不到名字的写原值", () => {
    const before = version("a", { deviceId: "d1", identifierIds: ["c1"] });
    const after = version("b", { deviceId: "d2", identifierIds: ["c1", "c2", "c3"] });
    const names = new Map([
      ["d2", "J929TVWWK3"],
      ["c2", "macos.bundle-id com.microsoft.VSCode"],
    ]);
    expect(describeChanges(before, after, names)).toEqual([
      "deviceId 改为 J929TVWWK3",
      "加入 macos.bundle-id com.microsoft.VSCode",
      "加入 c3",
    ]);
  });
});
