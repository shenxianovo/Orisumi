import { describe, expect, it } from "vitest";
import type { Thing } from "@/api/types";
import { observation, thing } from "@/test/fixtures";
import { groupThings, summarize, type Experience } from "./day";

const MIN = 60_000;
const from = Date.parse("2026-10-29T00:00:00Z");
const to = from + 24 * 60 * MIN;

const chrome = thing({ entityId: "chrome" });
const code = thing({ entityId: "code" });

function experience(id: string, start: number, end: number, target: Thing | null): Experience {
  return {
    observationId: id,
    observation: observation({ entityId: id }),
    observerId: "observer",
    writtenAt: end,
    contentType: "periods",
    contentTypeName: "前台应用时段",
    contentTitle: null,
    things: target ? [{ thing: target, title: target.entityId, typeName: "本机应用" }] : [],
    start,
    end,
  };
}

describe("summarize", () => {
  it("按事物分行、按时长排序，数出相邻两段之间的切换", () => {
    const day = summarize(
      [
        experience("a", from + 60 * MIN, from + 70 * MIN, chrome),
        experience("b", from + 70 * MIN, from + 100 * MIN, code),
        experience("c", from + 100 * MIN, from + 105 * MIN, chrome),
        experience("d", from + 105 * MIN, from + 106 * MIN, null),
      ],
      from,
      to,
    );
    expect(day.things.map((row) => [row.key, row.total / MIN])).toEqual([
      ["code", 30],
      ["chrome", 15],
    ]);
    expect(day.switches).toBe(2);
  });

  it("跨过这一天边界的时段只算这一天里的部分", () => {
    const day = summarize([experience("a", from - 30 * MIN, from + 30 * MIN, chrome)], from, to);
    expect(day.things[0]!.total).toBe(30 * MIN);
    expect(day.things[0]!.segments[0]!.start).toBe(from);
  });
});

describe("groupThings", () => {
  it("两台设备上的同一款软件合成一行，时长相加，彼此之间不算切换", () => {
    const macA = thing({ entityId: "qq-mac-a" });
    const macB = thing({ entityId: "qq-mac-b" });
    const qq = thing({ entityId: "qq" });
    const grouped = groupThings(
      [
        experience("a", from, from + 10 * MIN, macA),
        experience("b", from + 10 * MIN, from + 15 * MIN, macB),
        experience("c", from + 15 * MIN, from + 20 * MIN, chrome),
      ],
      (entry) => (entry.thing === macA || entry.thing === macB ? { thing: qq, title: "QQ", typeName: "应用" } : null),
    );
    const day = summarize(grouped, from, to);
    expect(day.things.map((row) => [row.key, row.total / MIN])).toEqual([
      ["qq", 15],
      ["chrome", 5],
    ]);
    expect(day.switches).toBe(1);
  });
});
