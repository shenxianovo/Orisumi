import type { ObservationVersion } from "@/api/types";
import { nameObservers, type ObserverIdentity } from "./observers";
import { MINUTE, parseTime } from "./time";

/** 最近 60 秒内保存过 Observation 的 Observer 算正在写入。 */
export const ACTIVE_WINDOW = MINUTE;
/** 时间轴末尾放大的一段。 */
export const LENS = 10 * MINUTE;

/** 一行按哪种时间画：写有事实时间的 Observer 按事实时间，只写事实时间未知的陈述的按保存时间。 */
export type TimeBasis = "fact" | "recorded";

export interface Span {
  start: number;
  end: number;
}

export interface ObserverRow {
  identity: ObserverIdentity;
  key: string;
  name: string;
  basis: TimeBasis;
  /** 按事实时间的时段。 */
  spans: Span[];
  /** 按事实时间的时间点，或者按保存时间的每份 Observation。 */
  ticks: number[];
  /** 今天的 Observation 数。 */
  count: number;
  /** 最近一次保存 Observation 的时刻，今天没有写入时为 0。 */
  lastAt: number;
}

export interface ActivityInput {
  /** 列出的全部 Observer。 */
  observers: ObserverIdentity[];
  /** Observation 的 observerId 对应的 Observer；读不到的 Observer 返回不可读取。 */
  identityOf: (observerId: string) => ObserverIdentity;
  /** 事实时间在今天的 Observation。 */
  timed: ObservationVersion[];
  /** 今天没有写有事实时间的 Observation 的 Observer，今天保存的 Observation。 */
  recorded: ObservationVersion[];
  now: number;
}

/** 合并重叠或间隔不超过 gap 的时段，画出来是连续的一段。 */
export function union(spans: Span[], gap = 0): Span[] {
  const sorted = [...spans].sort((a, b) => a.start - b.start);
  const merged: Span[] = [];
  for (const span of sorted) {
    const last = merged.at(-1);
    if (last && span.start - last.end <= gap) last.end = Math.max(last.end, span.end);
    else merged.push({ ...span });
  }
  return merged;
}

export function isActive(row: ObserverRow, now: number): boolean {
  return row.lastAt > 0 && now - row.lastAt < ACTIVE_WINDOW;
}

/** 每个 Observer 一行。正在写入的排在前面，其余按最近一次写入排列。 */
export function observerRows(input: ActivityInput): ObserverRow[] {
  const { now } = input;
  // 今天写了有事实时间的 Observation 的，按事实时间画。
  const timedKeys = new Set(input.timed.map((observation) => input.identityOf(observation.observerId).key));
  const rows = new Map<string, Omit<ObserverRow, "name">>();
  const row = (identity: ObserverIdentity) => {
    let entry = rows.get(identity.key);
    if (!entry) {
      entry = {
        identity,
        key: identity.key,
        basis: timedKeys.has(identity.key) ? "fact" : "recorded",
        spans: [],
        ticks: [],
        count: 0,
        lastAt: 0,
      };
      rows.set(identity.key, entry);
    }
    return entry;
  };
  const saved = (entry: Omit<ObserverRow, "name">, observation: ObservationVersion) => {
    entry.count += 1;
    entry.lastAt = Math.max(entry.lastAt, Math.min(parseTime(observation.recordedAt) ?? now, now));
  };

  for (const identity of input.observers) row(identity);

  for (const observation of input.timed) {
    const entry = row(input.identityOf(observation.observerId));
    saved(entry, observation);
    const start = parseTime(observation.startAt);
    const end = parseTime(observation.endAt);
    if (start !== null && (end === null || end > start)) entry.spans.push({ start, end: end ?? now });
    else if (start !== null || end !== null) entry.ticks.push((start ?? end)!);
  }

  for (const observation of input.recorded) {
    const entry = row(input.identityOf(observation.observerId));
    if (entry.basis !== "recorded") continue;
    saved(entry, observation);
    entry.ticks.push(Math.min(parseTime(observation.recordedAt) ?? now, now));
  }

  const list = [...rows.values()];
  const names = nameObservers(list.map((r) => r.identity));
  return list
    .map((r) => ({ ...r, name: names.get(r.key) ?? r.key }))
    .sort(
      (a, b) =>
        Number(isActive(b, now)) - Number(isActive(a, now)) ||
        b.lastAt - a.lastAt ||
        a.name.localeCompare(b.name, "zh") ||
        a.key.localeCompare(b.key),
    );
}
