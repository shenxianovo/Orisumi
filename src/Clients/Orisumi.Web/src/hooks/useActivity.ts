import { keepPreviousData, useQuery } from "@tanstack/react-query";
import type { OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isObservation, type ObservationVersion } from "@/api/types";
import { UNREADABLE_IDENTITY, type ObserverIdentity } from "@/lib/observers";
import { dayRange, parseTime } from "@/lib/time";
import { useDayObservations } from "./useDay";
import { OBSERVERS_INTERVAL, useObservers } from "./useObservers";
import { pool } from "./useTypes";

/**
 * 一个 Observer 保存时间在 [from, to) 内的 Observation。引用方按 VersionId 降序排列，也就是保存的先后，
 * 读到更早的就停下。
 */
async function savedBetween(source: OrisumiSource, observerId: string, from: number, to: number) {
  const found: ObservationVersion[] = [];
  let after: string | undefined;
  do {
    const page = await source.listReferrers(observerId, {
      path: "/observerId",
      order: "desc",
      limit: 200,
      ...(after ? { after } : {}),
    });
    for (const { version } of page.referrers) {
      const at = parseTime(version.recordedAt);
      if (at === null || !isObservation(version)) continue;
      if (at < from) return found;
      if (at < to) found.push(version);
    }
    after = page.next ?? undefined;
  } while (after);
  return found;
}

/**
 * Observer 页面的数据：全部 Observer、事实时间在今天的 Observation，以及今天没有写有事实时间的 Observation 的 Observer
 * 今天保存的 Observation。各行由 observerRows 组合。
 */
export function useActivity(now: number) {
  const source = useSource();
  const day = dayRange(now);
  const observers = useObservers();
  const timed = useDayObservations(day, true);

  const timedIds = new Set((timed.data ?? []).map((observation) => observation.observerId));
  const others = [...(observers.data?.keys() ?? [])].filter((id) => !timedIds.has(id)).sort();
  const recorded = useQuery({
    queryKey: ["observers", "recorded", day.key, others],
    queryFn: async () => (await pool(others, 4, (id) => savedBetween(source, id, day.start, day.end))).flat(),
    enabled: !!observers.data && !!timed.data,
    refetchInterval: OBSERVERS_INTERVAL,
    placeholderData: keepPreviousData,
  });

  const identityOf = (observerId: string): ObserverIdentity =>
    observers.data?.get(observerId)?.identity ?? UNREADABLE_IDENTITY;
  return {
    day,
    observers: [...(observers.data?.values() ?? [])].map((info) => info.identity),
    identityOf,
    timed: timed.data ?? [],
    recorded: recorded.data ?? [],
    loading: observers.isPending || timed.isPending,
    error: observers.error ?? timed.error ?? recorded.error,
  };
}
