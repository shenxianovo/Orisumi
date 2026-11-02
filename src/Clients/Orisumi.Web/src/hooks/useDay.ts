import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { readAllPages } from "@/api/client";
import { useSource } from "@/api/source";
import type { Observation } from "@/api/types";
import { type DayThing, type Experience, type PointGroup } from "@/lib/day";
import { EPISODES } from "@/lib/episodes";
import { outgoingReferences } from "@/lib/references";
import { roleOf } from "@/lib/roles";
import { dayOf, dayRange, parseTime, SECOND } from "@/lib/time";
import { businessTitle, thingTitle } from "@/lib/titles";
import { useNow } from "./useNow";
import { useObservers } from "./useObservers";
import { pool, readThings, schemaOf, useTypes, type TypeIndex } from "./useTypes";

export const DAY_INTERVAL = 5 * SECOND;

export interface DayData {
  experiences: Experience[];
  points: PointGroup[];
}

/** 事实时间在这一天的全部 Observation。今天的每 5 秒刷新，日页面和 Observer 页面共用。 */
export function useDayObservations(range: ReturnType<typeof dayRange>, today: boolean) {
  const source = useSource();
  return useQuery({
    queryKey: ["observations", "day", range.key],
    queryFn: () =>
      readAllPages((after) =>
        source.listObservations({ from: range.from, to: range.to, limit: 1000, ...(after ? { after } : {}) }),
      ) as Promise<Observation[]>,
    refetchInterval: today ? DAY_INTERVAL : false,
  });
}

/**
 * 一天的经历：读出这一天的全部 Observation，有时段的那些解析出类型和它们引用的事物。
 * 引用的事物连同名字一次批量读出。今天的数据每 5 秒刷新。
 */
export function useDay(key: string) {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  const observers = useObservers();
  const now = useNow(60_000);
  const range = dayOf(key) ?? dayRange(now);
  const today = range.key === dayRange(now).key;

  const observations = useDayObservations(range, today);

  const resolved = useQuery({
    queryKey: ["day", range.key, observations.dataUpdatedAt, observers.dataUpdatedAt],
    queryFn: () => resolve(observations.data!, types.data!),
    enabled: !!observations.data && !!types.data && !!observers.data,
    placeholderData: keepPreviousData,
    staleTime: Infinity,
  });

  async function resolve(list: Observation[], index: TypeIndex): Promise<DayData> {
    const now = Date.now();
    const timed = list.filter(isPeriod);
    const typed = await pool(timed, 12, (observation) => schemaOf(client, source, observation));
    // 几件事是对这一天的理解，不是一段经历。
    const kept = timed.flatMap((observation, i) =>
      typed[i]?.content.resourceName === EPISODES ? [] : [{ observation, schema: typed[i] }],
    );
    const periods = kept.map((entry) => entry.observation);
    const schemas = kept.map((entry) => entry.schema);
    const references = periods.map((observation, i) =>
      outgoingReferences(observation, schemas[i]?.content.fields).filter(
        (reference) => reference.target === "thing" && reference.path !== "observerId",
      ),
    );
    const things = await readThings(
      client,
      source,
      references.flatMap((list) => list.map((reference) => reference.id)),
    );

    const experiences = periods.map((observation, i): Experience => {
      const schema = schemas[i]?.content;
      const targets = new Map<string, DayThing>();
      for (const reference of references[i]!) {
        const thing = things.get(reference.id);
        if (!thing || roleOf(thing) !== "thing" || targets.has(thing.thingId)) continue;
        const typeName = index.bySchemaId.get(thing.schemaId)?.content.displayName ?? "事物";
        targets.set(thing.thingId, { thing, title: thingTitle(thing, typeName), typeName });
      }
      return {
        observationId: observation.observationId,
        observation,
        observerId: observation.observerId,
        writtenAt: Math.min(parseTime(observation.recordedAt) ?? now, now),
        contentType: schema?.resourceName ?? "unknown",
        contentTypeName: schema?.displayName ?? "Observation",
        contentTitle: businessTitle(observation.content, schema?.fields, schema?.resourceName)?.text ?? null,
        things: [...targets.values()],
        start: parseTime(observation.startAt)!,
        end: parseTime(observation.endAt) ?? now,
        denied: (observation.deniedRanges ?? []).map((range) => ({
          start: parseTime(range.startAt)!,
          end: parseTime(range.endAt)!,
        })),
      };
    });

    const pointsByObserver = new Map<string, number[]>();
    for (const observation of list) {
      if (isPeriod(observation)) continue;
      const at = parseTime(observation.startAt) ?? parseTime(observation.endAt);
      if (at === null) continue;
      pointsByObserver.set(observation.observerId, [...(pointsByObserver.get(observation.observerId) ?? []), at]);
    }
    const points: PointGroup[] = [...pointsByObserver].map(([observerId, times]) => ({
      key: observerId,
      title: observers.data?.get(observerId)?.identity.typeName ?? "Observer",
      times,
    }));
    return { experiences, points };
  }

  return {
    range,
    today,
    data: resolved.data,
    loading: observations.isPending || resolved.isPending,
    error: observations.error ?? resolved.error ?? types.error,
  };
}

/** 有时段：开始已知，结束未知或晚于开始。 */
function isPeriod(statement: Observation): boolean {
  const start = parseTime(statement.startAt);
  const end = parseTime(statement.endAt);
  return start !== null && (end === null || end > start);
}
