import { describe, expect, it } from "vitest";
import type { Host, Instance } from "@/api/management";
import { collectionProblems } from "./collection";

const host = (overrides: Partial<Host> = {}): Host => ({
  hostId: "mac",
  name: "J929",
  kind: "device",
  revoked: false,
  connected: true,
  lastSeenAt: null,
  ...overrides,
});

const instance = (overrides: Partial<Instance> = {}): Instance => ({
  thingId: "a",
  hostId: "mac",
  packageId: "foreground-state",
  name: "Foreground State",
  enabled: true,
  settings: {},
  revision: 1,
  removed: false,
  reportedAt: null,
  status: { state: "running" },
  ...overrides,
});

describe("collectionProblems", () => {
  it("都在线、都在运行时没有问题", () => {
    expect(collectionProblems([host()], [instance()])).toEqual([]);
  });

  it("写出离线的机器，同名的只写一次，已移除的不算", () => {
    const hosts = [
      host({ hostId: "a", connected: false }),
      host({ hostId: "b", connected: false }),
      host({ hostId: "c", name: "旧机器", connected: false, revoked: true }),
    ];
    expect(collectionProblems(hosts, [])).toEqual(["J929 离线"]);
  });

  it("写出启用的实例报告的错误，停用的和离线机器上的不算", () => {
    const failed = { state: "failed", error: "读不到前台窗口" } as const;
    expect(
      collectionProblems(
        [host()],
        [instance({ status: failed }), instance({ thingId: "b", name: "停用的", enabled: false, status: failed })],
      ),
    ).toEqual(["Foreground State：读不到前台窗口"]);
    expect(collectionProblems([host({ connected: false })], [instance({ status: failed })])).toEqual(["J929 离线"]);
  });
});
