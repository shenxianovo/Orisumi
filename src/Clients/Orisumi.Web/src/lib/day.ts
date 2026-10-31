import type { ObservationVersion, Thing } from "@/api/types";
import { thingColor } from "./roles";

// 一天的经历：有时段的 Observation 和它引用的事物。今天页面的应用只看前台应用时段，事物的面板看所有经历。

export const PERIODS = "foreground-application-periods";

/** 经历引用的一个事物，以及它在界面上的名字和类型名。 */
export interface DayThing {
  thing: Thing;
  title: string;
  typeName: string;
}

/** 解析过的一份经历。things 是它引用的事物（例如本机应用）。 */
export interface Experience {
  /** Observation 的 EntityId。 */
  observationId: string;
  /** 已保存的当前版本。 */
  observation: ObservationVersion;
  observerId: string;
  /** 保存时间。 */
  writtenAt: number;
  contentType: string;
  contentTypeName: string;
  contentTitle: string | null;
  things: DayThing[];
  start: number;
  end: number;
}

/** 只有时间点的 Observation，按 Observer 汇总。 */
export interface PointGroup {
  key: string;
  title: string;
  times: number[];
}

export interface Segment {
  start: number;
  end: number;
  observationId: string;
  label: string;
}

export interface Row {
  key: string;
  thing: Thing;
  title: string;
  typeName: string;
  color: string;
  total: number;
  segments: Segment[];
}

export interface DaySummary {
  things: Row[];
  /** 事物之间切换的次数：同一个 Observer 的相邻两段指向不同的事物。 */
  switches: number;
}

export function clip(start: number, end: number, from: number, to: number): [number, number] | null {
  const s = Math.max(start, from);
  const e = Math.min(end, to);
  return e > s ? [s, e] : null;
}

export function summarize(experiences: readonly Experience[], from: number, to: number): DaySummary {
  const things = new Map<string, Row>();
  const sorted = [...experiences].sort((a, b) => a.start - b.start);
  let switches = 0;
  const last = new Map<string, string>();

  for (const experience of sorted) {
    const range = clip(experience.start, experience.end, from, to);
    if (!range || !experience.things.length) continue;
    for (const { thing, title, typeName } of experience.things) {
      const row = things.get(thing.entityId) ?? {
        key: thing.entityId,
        thing,
        title,
        typeName,
        color: thingColor(title),
        total: 0,
        segments: [],
      };
      row.segments.push({
        start: range[0],
        end: range[1],
        observationId: experience.observationId,
        label: experience.contentTitle ?? title,
      });
      row.total += range[1] - range[0];
      things.set(thing.entityId, row);
    }
    const observer = experience.observerId;
    const target = experience.things[0]!.thing.entityId;
    if (last.has(observer) && last.get(observer) !== target) switches++;
    last.set(observer, target);
  }

  return { things: [...things.values()].sort((a, b) => b.total - a.total), switches };
}

/** 事物换成什么，例如本机应用换成它所属的应用产品；返回 null 时保持原样。 */
export type ThingGroup = (thing: DayThing) => DayThing | null;

/** 把经历引用的事物换成它们的合并对象，例如两台 Mac 上的 QQ 都换成 QQ 这个产品。同一份经历里重复的只留一个。 */
export function groupThings(experiences: readonly Experience[], group: ThingGroup): Experience[] {
  return experiences.map((experience) => {
    if (!experience.things.length) return experience;
    const things = new Map<string, DayThing>();
    for (const thing of experience.things) {
      const target = group(thing) ?? thing;
      if (!things.has(target.thing.entityId)) things.set(target.thing.entityId, target);
    }
    return { ...experience, things: [...things.values()] };
  });
}
