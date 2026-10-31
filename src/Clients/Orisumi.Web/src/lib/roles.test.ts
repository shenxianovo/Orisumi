import { describe, expect, it } from "vitest";
import { PERSON_SCHEMA, PROGRAM_SCHEMA, USER_KEY } from "./common";
import { differences, distinctSegment, roleOf } from "./roles";
import { inputSet, observation, schema, thing } from "@/test/fixtures";

describe("roleOf", () => {
  it("按类别和事实时间", () => {
    expect(roleOf(observation())).toBe("experience");
    expect(roleOf(observation({ startAt: null, endAt: null }))).toBe("statement");
    expect(roleOf(inputSet([]))).toBe("evidence");
    expect(roleOf(schema({ resourceName: "x" }))).toBe("definition");
  });

  it("程序实例和用户本人是来源，其余事物是事物", () => {
    expect(roleOf(thing({ schemaId: PROGRAM_SCHEMA }))).toBe("source");
    expect(roleOf(thing({ schemaId: PERSON_SCHEMA, keys: [USER_KEY] }))).toBe("source");
    expect(roleOf(thing({ schemaId: PERSON_SCHEMA }))).toBe("thing");
    expect(roleOf(thing())).toBe("thing");
  });
});

describe("distinctSegment", () => {
  it("只留下各条路径不同的那一段", () => {
    const release = "/Users/a/Build/Products/Release/Orisumi.app/Contents/MacOS/Orisumi";
    const debug = "/Users/a/Build/Products/Debug/Orisumi.app/Contents/MacOS/Orisumi";
    expect(distinctSegment(release, [release, debug])).toBe("…/Release/…");
    expect(distinctSegment(debug, [release, debug])).toBe("…/Debug/…");
    expect(distinctSegment(release, [release])).toBeNull();
  });
});

describe("differences", () => {
  it("同名的安装写出可执行文件路径里不同的那一段", () => {
    const app = (path: string) => ({
      identifiers: [
        { scheme: "macos.bundle-id", value: "com.shenxianovo.orisumi" },
        { scheme: "macos.executable-path", value: path },
      ],
    });
    expect(
      differences([
        app("/a/Build/Products/Release/Orisumi.app/Contents/MacOS/Orisumi"),
        app("/a/Build/Products/Debug/Orisumi.app/Contents/MacOS/Orisumi"),
      ]),
    ).toEqual(["…/Release/…", "…/Debug/…"]);
  });

  it("内容完全相同时没有补充", () => {
    expect(differences([{ name: "x" }, { name: "x" }])).toEqual([null, null]);
  });
});
