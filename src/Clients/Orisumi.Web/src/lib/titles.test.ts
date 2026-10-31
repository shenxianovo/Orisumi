import { describe, expect, it } from "vitest";
import type { FieldDefinition } from "@/api/types";
import { thing } from "@/test/fixtures";
import { businessTitle, thingTitle } from "./titles";

const string = (): FieldDefinition => ({ type: "string", description: "" });
const reference = (): FieldDefinition => ({ type: "reference", target: "thing", description: "" });

describe("businessTitle", () => {
  it("取第一个不是引用的文字字段", () => {
    const fields = { localApplicationId: reference(), name: string() };
    expect(businessTitle({ localApplicationId: "a", name: "VS Code" }, fields, "periods")).toEqual({
      text: "VS Code",
      key: "name",
    });
  });

  it("没有文字字段时没有名字", () => {
    expect(businessTitle({ localApplicationId: "a" }, { localApplicationId: reference() }, "periods")).toBeNull();
  });
});

describe("thingTitle", () => {
  it("有名字时用名字，没有时写识别依据的值，再没有时写类型名", () => {
    const key = { scope: null, scheme: "macos.bundle-id", value: "com.google.Chrome" };
    expect(thingTitle(thing({ name: "Chrome", keys: [key] }), "线索")).toBe("Chrome");
    expect(thingTitle(thing({ keys: [key] }), "线索")).toBe("com.google.Chrome");
    expect(thingTitle(thing(), "线索")).toBe("线索");
  });

  it("识别依据有几行时写第一行", () => {
    const key = { scope: "mac", scheme: "macos.installation", value: "com.microsoft.VSCode\n/Applications/Code.app" };
    expect(thingTitle(thing({ keys: [key] }), "本机应用")).toBe("com.microsoft.VSCode");
  });
});
