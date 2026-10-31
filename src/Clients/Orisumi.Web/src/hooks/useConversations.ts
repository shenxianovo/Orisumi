import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, readAllPages, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import type { BinaryValue, ObservationVersion } from "@/api/types";
import {
  groupSessions,
  resolvePointer,
  toInteraction,
  type InteractionContent,
  type SessionMeta,
} from "@/lib/conversations";
import { dayOf, dayRange } from "@/lib/time";
import { DAY_INTERVAL } from "./useDay";
import { useNow } from "./useNow";
import { pool, readThings } from "./useTypes";

export const INTERACTIONS = "assistant-interactions";
const SUBAGENTS = "assistant-subagents";

/** 说明这个会话是子 Agent 的那份关系。 */
async function subagentOf(source: OrisumiSource, sessionId: string): Promise<SessionMeta> {
  const page = await source.listReferrers(sessionId, {
    resourceName: SUBAGENTS,
    path: "/content/sessionId",
    order: "desc",
    limit: 1,
  });
  const relation = page.referrers[0]?.version.content as
    { parentSessionId: string; agentName: string | null } | undefined;
  return { parentEntityId: relation?.parentSessionId ?? null, agentName: relation?.agentName ?? null };
}

/**
 * 一天的助手会话：按事实时间读出这一天的交互，读出它们的会话，再按会话分组。
 * 会话的名字随事物一起读到；子 Agent 关系缓存 30 秒。
 */
export function useConversations(key: string) {
  const source = useSource();
  const client = useQueryClient();
  const now = useNow(60_000);
  const range = dayOf(key) ?? dayRange(now);
  const today = range.key === dayRange(now).key;

  const observations = useQuery({
    queryKey: ["observations", "day", range.key, INTERACTIONS],
    queryFn: () =>
      readAllPages((after) =>
        source.listObservations({
          from: range.from,
          to: range.to,
          resourceName: INTERACTIONS,
          limit: 1000,
          ...(after ? { after } : {}),
        }),
      ).catch((error: unknown) => {
        // 资源名还没有登记时按没有数据处理。
        if (error instanceof ApiError && error.status === 404) return [];
        throw error;
      }) as Promise<ObservationVersion<InteractionContent>[]>,
    refetchInterval: today ? DAY_INTERVAL : false,
  });

  const groups = useQuery({
    queryKey: ["conversations", range.key, observations.dataUpdatedAt],
    queryFn: async () => {
      const interactions = observations.data!.map(toInteraction);
      const sessionIds = [...new Set(interactions.map((i) => i.version.content.sessionId))];
      const sessions = await readThings(client, source, sessionIds);
      const metas = new Map<string, SessionMeta>();
      await pool(sessionIds, 8, async (sessionId) => {
        metas.set(
          sessionId,
          await client.fetchQuery({
            queryKey: ["session-meta", sessionId],
            staleTime: 30_000,
            queryFn: () => subagentOf(source, sessionId),
          }),
        );
      });
      return groupSessions(interactions, sessions, metas);
    },
    enabled: !!observations.data,
    placeholderData: keepPreviousData,
    staleTime: Infinity,
  });

  return {
    range,
    today,
    groups: groups.data,
    loading: observations.isPending || (!!observations.data && groups.isPending),
    error: observations.error ?? groups.error,
  };
}

/** 一份原始记录解析后的 JSON。摘要确定了字节，所以读到后一直缓存。 */
export function useRawEntry(entry: BinaryValue | undefined, enabled: boolean) {
  const source = useSource();
  return useQuery({
    queryKey: ["raw-entry", entry?.digest],
    queryFn: async () => JSON.parse(await (await source.getContent(entry!.digest)).text()) as unknown,
    enabled: enabled && !!entry,
    staleTime: Infinity,
  });
}

/** 片段在原始记录中的值。 */
export function usePartValue(
  content: InteractionContent,
  part: { entryIndex: number; pointer: string },
  enabled: boolean,
) {
  const raw = useRawEntry(content.sourceEntries[part.entryIndex], enabled);
  return { ...raw, value: raw.data === undefined ? undefined : resolvePointer(raw.data, part.pointer) };
}
