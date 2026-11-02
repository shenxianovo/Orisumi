import { useCallback, useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient, type UseQueryResult } from "@tanstack/react-query";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import type { NarrativeEpisode, Observation } from "@/api/types";
import { EPISODES } from "@/lib/episodes";
import type { dayRange } from "@/lib/time";
import { MINUTE, SECOND } from "@/lib/time";
import { observations } from "./useApplications";

type DayRange = ReturnType<typeof dayRange>;

const EPISODES_INTERVAL = MINUTE;
/** 另一次写作还没写完时，隔这么久再看。 */
const WAITING_INTERVAL = 5 * SECOND;

/** 这一天当前理解中的几件事，按开始时间排列。今天的每分钟刷新。 */
export function useEpisodes(range: DayRange, today: boolean) {
  const source = useSource();
  return useQuery({
    queryKey: ["episodes", range.key],
    queryFn: async () => {
      const list = await observations(source, {
        from: range.from,
        to: range.to,
        resourceName: EPISODES,
      });
      return list.sort((a, b) => (a.startAt ?? "").localeCompare(b.startAt ?? ""));
    },
    refetchInterval: today ? EPISODES_INTERVAL : false,
  });
}

export type NarrativeState =
  | { kind: "idle" }
  | { kind: "writing"; episodes: NarrativeEpisode[] }
  /** 另一次写作在写重叠的范围，等它写完。 */
  | { kind: "waiting" }
  | { kind: "failed"; detail: string }
  | { kind: "unavailable" };

const IDLE: NarrativeState = { kind: "idle" };

/**
 * 请求叙事 Observer 写一天：这一天有记录、还没有写下的几件事时自动请求一次，之后只有“重写”才再请求。
 * 离开这一天时断开连接，叙事 Observer 照样写完，回来时读取结果。
 */
export function useNarrative(range: DayRange, episodes: UseQueryResult<Observation[]>, hasRecords: boolean) {
  const source = useSource();
  const client = useQueryClient();
  const [stage, setStage] = useState<{ key: string; state: NarrativeState }>({ key: range.key, state: IDLE });
  const asked = useRef(new Set<string>());
  const running = useRef<AbortController | null>(null);
  const state = stage.key === range.key ? stage.state : IDLE;
  const { key, from, to } = range;

  const write = useCallback(async () => {
    running.current?.abort();
    const abort = new AbortController();
    running.current = abort;
    asked.current.add(key);
    const update = (next: (state: NarrativeState) => NarrativeState) =>
      setStage((current) => ({ key, state: next(current.key === key ? current.state : IDLE) }));
    update(() => ({ kind: "writing", episodes: [] }));
    let finished = false;
    try {
      const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
      for await (const event of source.writeNarrative({ from, to, timeZone }, abort.signal)) {
        if (event.type === "episode")
          update((state) =>
            state.kind === "writing" ? { ...state, episodes: [...state.episodes, event.episode] } : state,
          );
        if (event.type === "saved") {
          finished = true;
          await client.invalidateQueries({ queryKey: ["episodes", key] });
          update(() => IDLE);
        }
        if (event.type === "failed") {
          finished = true;
          update(() => ({ kind: "failed", detail: event.detail }));
        }
      }
      if (!finished) update(() => ({ kind: "waiting" }));
    } catch (error) {
      if (abort.signal.aborted) {
        asked.current.delete(key);
        return;
      }
      const code = error instanceof ApiError ? error.problem?.code : undefined;
      if (code === "narrative_unavailable") update(() => ({ kind: "unavailable" }));
      else if (code === "narrative_in_progress") update(() => ({ kind: "waiting" }));
      else update(() => ({ kind: "failed", detail: error instanceof Error ? error.message : String(error) }));
    }
  }, [source, client, key, from, to]);

  const empty = episodes.data?.length === 0;
  useEffect(() => {
    if (!empty || !hasRecords || state.kind !== "idle" || asked.current.has(key)) return;
    const timer = setTimeout(() => void write(), 0);
    return () => clearTimeout(timer);
  }, [empty, hasRecords, state.kind, key, write]);

  const { refetch } = episodes;
  useEffect(() => {
    if (state.kind !== "waiting") return;
    const timer = setTimeout(() => {
      void refetch().then((result) => {
        if (result.data?.length) setStage({ key, state: IDLE });
        else void write();
      });
    }, WAITING_INTERVAL);
    return () => clearTimeout(timer);
  }, [state.kind, key, refetch, write]);

  useEffect(() => () => running.current?.abort(), [key]);

  return { state, write };
}
