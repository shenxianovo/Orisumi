import { describe, expect, it } from "vitest";
import { categoryInfo, categoryOf } from "./categories";
import { nameObservers, observerIdentity, UNREADABLE_IDENTITY, UNREADABLE_KEY } from "./observers";

describe("nameObservers", () => {
  it("名称取类型名", () => {
    const names = nameObservers([observerIdentity("0192b056-7300-7000-8000-00000000a1b2", "前台状态", null)]);
    expect(names.get("0192b056-7300-7000-8000-00000000a1b2")).toBe("前台状态");
  });

  it("同名的 Observer 加 thingId 的末 4 位", () => {
    const names = nameObservers([
      observerIdentity("0192b056-7300-7000-8000-00000000a1b2", "前台状态", null),
      observerIdentity("0192b056-7300-7000-8000-00000000c3d4", "前台状态", null),
    ]);
    expect(names.get("0192b056-7300-7000-8000-00000000a1b2")).toBe("前台状态 a1b2");
    expect(names.get("0192b056-7300-7000-8000-00000000c3d4")).toBe("前台状态 c3d4");
  });

  it("读不到的 Observer 显示为不可读取", () => {
    expect(nameObservers([UNREADABLE_IDENTITY]).get(UNREADABLE_KEY)).toBe("不可读取");
  });
});

describe("observerIdentity", () => {
  it("类别按程序实例的安装包", () => {
    expect(observerIdentity("observer", "前台状态", "foreground-state")).toEqual({
      key: "observer",
      thingId: "observer",
      typeName: "前台状态",
      source: "foreground-state",
      group: "desktop",
    });
  });
});

describe("categoryOf", () => {
  it("按安装包查映射表，表中没有的归入其他", () => {
    expect(categoryOf("foreground-state")).toBe("desktop");
    expect(categoryOf("application-identity")).toBe("inference");
    expect(categoryOf("unknown")).toBe("other");
    expect(categoryOf(null)).toBe("other");
    expect(categoryInfo("other").label).toBe("其他");
  });
});
