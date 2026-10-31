import { describe, expect, it } from "vitest";
import type { ObservationVersion, Thing } from "@/api/types";
import { observation, thing } from "@/test/fixtures";
import { buildCatalog, installDetails, type CatalogInput } from "./applications";

const clue = (scheme: string, value: string) => thing({ keys: [{ scope: null, scheme, value }] });
const qqMac = clue("macos.bundle-id", "com.tencent.qq");
const qqAndroid = clue("android.package-name", "com.tencent.mobileqq");
const lark = clue("macos.bundle-id", "com.electron.lark");

const product = (name: string) => thing({ name });
const membership = (identifier: Thing, target: Thing, recordedAt = "2026-10-29T01:00:00.000Z") =>
  observation({
    recordedAt,
    startAt: null,
    endAt: null,
    content: { identifierId: identifier.entityId, productId: target.entityId },
  });

function install(identifiers: Thing[], path: string): [Thing, ObservationVersion] {
  const app = thing();
  return [
    app,
    observation({
      startAt: null,
      endAt: null,
      content: {
        localApplicationId: app.entityId,
        deviceId: "device",
        identifierIds: identifiers.map((i) => i.entityId),
        executablePath: path,
      },
    }),
  ];
}

function catalogOf(products: Thing[], memberships: ObservationVersion[], installs: [Thing, ObservationVersion][]) {
  const input: CatalogInput = {
    products,
    memberships,
    clues: new Map([qqMac, qqAndroid, lark].map((c) => [c.entityId, c])),
    installs: installs.map(([app]) => app),
    descriptions: installs.map(([, description]) => description),
  };
  return buildCatalog(input);
}

describe("归属", () => {
  it("跨设备线索相同的安装属于同一个产品，路径不参与", () => {
    const qq = product("QQ");
    const a = install([qqMac], "/Applications/QQ.app/Contents/MacOS/QQ");
    const b = install([qqMac], "/Users/me/Applications/QQ.app/Contents/MacOS/QQ");
    const catalog = catalogOf([qq], [membership(qqMac, qq)], [a, b]);
    expect(catalog.productOf(a[0])?.name).toBe("QQ");
    expect(catalog.productOf(b[0])?.name).toBe("QQ");
    expect(catalog.installsOf(qqMac.entityId)).toHaveLength(2);
  });

  it("一个产品的线索来自各自的线索归属", () => {
    const qq = product("QQ");
    const earlier = membership(qqMac, qq, "2026-10-29T01:00:00.000Z");
    const later = membership(qqAndroid, qq, "2026-10-29T02:00:00.000Z");
    const catalog = catalogOf([qq], [earlier, later], []);
    expect(catalog.products[0]!.memberships.map((m) => m.clue.value)).toEqual([
      "com.tencent.qq",
      "com.tencent.mobileqq",
    ]);
  });

  it("没有线索归属的产品不列出", () => {
    expect(catalogOf([product("QQ"), product("Lark")], [], []).products).toEqual([]);
  });

  it("指向的产品已删除时，线索未归属", () => {
    const app = install([qqMac], "/a");
    const catalog = catalogOf([], [membership(qqMac, product("已删除"))], [app]);
    expect(catalog.productOf(app[0])).toBeNull();
  });

  it("同一条线索的几个安装写出路径里不同的那一段", () => {
    const catalog = catalogOf([], [], [install([qqMac], "/A/Release/QQ"), install([qqMac], "/A/Debug/QQ")]);
    expect(installDetails(catalog.installs)).toEqual(["…/Release/…", "…/Debug/…"]);
    expect(installDetails(catalog.installs.slice(0, 1))).toEqual([null]);
  });
});
