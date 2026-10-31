import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { isUnreadable, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import type { BinaryValue, ListEntitiesQuery, Thing } from "@/api/types";
import { THING_STALE } from "./useTypes";

const retry = (count: number, error: unknown) => !isUnreadable(error) && count < 2;

/** 版本不会改变，读到后一直缓存。 */
export function versionQuery(source: OrisumiSource, versionId: string | null | undefined) {
  return {
    queryKey: ["version", versionId],
    queryFn: () => source.getVersion(versionId!),
    enabled: !!versionId,
    staleTime: Infinity,
    retry,
  };
}

export function useVersion(versionId: string | null | undefined) {
  return useQuery(versionQuery(useSource(), versionId));
}

/**
 * 一份二进制内容，按描述中的媒体类型做成页面里可用的地址。摘要确定了字节，所以读到后一直缓存，地址也不回收。
 */
export function useContentUrl(value: BinaryValue | null | undefined) {
  const source = useSource();
  return useQuery({
    queryKey: ["content", value?.digest, value?.mediaType],
    queryFn: async () =>
      URL.createObjectURL(new Blob([await source.getContent(value!.digest)], { type: value!.mediaType })),
    enabled: !!value,
    staleTime: Infinity,
    gcTime: Infinity,
    retry,
  });
}

/** 同一轮渲染里要读的事物攒成一次批量读取。 */
interface Waiter {
  resolve: (thing: Thing | null) => void;
  reject: (error: unknown) => void;
}
const batches = new WeakMap<OrisumiSource, Map<string, Waiter[]>>();

function loadThing(source: OrisumiSource, entityId: string): Promise<Thing | null> {
  return new Promise((resolve, reject) => {
    let waiting = batches.get(source);
    if (!waiting) {
      const batch = new Map<string, Waiter[]>();
      waiting = batch;
      batches.set(source, batch);
      queueMicrotask(async () => {
        batches.delete(source);
        const ids = [...batch.keys()];
        try {
          for (let i = 0; i < ids.length; i += 256)
            for (const result of (await source.readThings(ids.slice(i, i + 256))).results)
              for (const waiter of batch.get(result.entityId) ?? []) waiter.resolve(result.thing ?? null);
        } catch (error) {
          for (const list of batch.values()) for (const waiter of list) waiter.reject(error);
        }
      });
    }
    waiting.set(entityId, [...(waiting.get(entityId) ?? []), { resolve, reject }]);
  });
}

/** 一个事物；名字随当前理解变化，所以 30 秒后重读。读不到时为 null。 */
export function thingQuery(source: OrisumiSource, entityId: string | null | undefined) {
  return {
    queryKey: ["thing", entityId],
    queryFn: () => loadThing(source, entityId!),
    enabled: !!entityId,
    staleTime: THING_STALE,
    retry,
  };
}

export function useThing(entityId: string | null | undefined) {
  return useQuery(thingQuery(useSource(), entityId));
}

/** 事物返回它自己，有版本的实体返回当前版本。 */
export function useEntity(entityId: string) {
  const source = useSource();
  return useQuery({ queryKey: ["entity", entityId], queryFn: () => source.getEntity(entityId), retry });
}

export function useEntityVersions(entityId: string | null | undefined) {
  const source = useSource();
  return useQuery({
    queryKey: ["entity", entityId, "versions"],
    queryFn: () => source.listEntityVersions(entityId!).then((r) => r.versions),
    enabled: !!entityId,
    retry,
  });
}

export function useReferrers(entityId: string | null) {
  const source = useSource();
  return useInfiniteQuery({
    queryKey: ["entity", entityId, "referrers"],
    queryFn: ({ pageParam }) =>
      source.listReferrers(entityId!, { limit: 50, ...(pageParam ? { after: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.next ?? undefined,
    enabled: !!entityId,
    retry,
  });
}

/** 一类实体：一种 Core 类别，或者一种业务类型；things 时列的是这种类型的事物。 */
export type EntityFilter = Pick<ListEntitiesQuery, "category" | "resourceName"> & { things?: boolean };

export const COUNT_LIMIT = 100;

/** 一类实体的数量，最多数到 100；更多时 more 为 true。 */
export function useEntityCount(filter: EntityFilter) {
  const source = useSource();
  return useQuery({
    queryKey: ["entities", filter, "count"],
    queryFn: async () => {
      const { things, ...query } = filter;
      const page = things
        ? await source.listThings({ ...query, limit: COUNT_LIMIT }).then((p) => ({ items: p.things, next: p.next }))
        : await source
            .listEntities({ ...query, limit: COUNT_LIMIT })
            .then((p) => ({ items: p.versions, next: p.next }));
      return { count: page.items.length, more: page.next !== null };
    },
    retry,
  });
}
