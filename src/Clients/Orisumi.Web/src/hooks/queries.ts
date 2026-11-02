import { useInfiniteQuery, useQuery, type QueryClient } from "@tanstack/react-query";
import { ApiError, isUnreadable, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { handleOf, isObservation, isThing, type BinaryValue, type Item, type Thing } from "@/api/types";
import { THING_STALE } from "./useTypes";

const retry = (count: number, error: unknown) => !isUnreadable(error) && count < 2;

/** 这些查询的结果不随复核和删除变化：Observation、修订和内容不可变，类型和 Observer 另有刷新。 */
const STABLE = new Set([
  "observation",
  "schema-revision",
  "input-set",
  "content",
  "raw-entry",
  "types",
  "observers",
  "instances",
  "hosts",
]);

/** 复核或删除之后，默认视图由后端重新算出，重读页面上依赖它的数据。 */
export function refreshView(client: QueryClient) {
  void client.invalidateQueries({ predicate: (query) => !STABLE.has(query.queryKey[0] as string) });
}

/** Observation 不会改变，读到后一直缓存。 */
export function observationQuery(source: OrisumiSource, observationId: string | null | undefined) {
  return {
    queryKey: ["observation", observationId],
    queryFn: () => source.getObservation(observationId!),
    enabled: !!observationId,
    staleTime: Infinity,
    retry,
  };
}

export function useObservation(observationId: string | null | undefined) {
  return useQuery(observationQuery(useSource(), observationId));
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

function loadThing(source: OrisumiSource, thingId: string): Promise<Thing | null> {
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
              for (const waiter of batch.get(result.thingId) ?? []) waiter.resolve(result.thing ?? null);
        } catch (error) {
          for (const list of batch.values()) for (const waiter of list) waiter.reject(error);
        }
      });
    }
    waiting.set(thingId, [...(waiting.get(thingId) ?? []), { resolve, reject }]);
  });
}

/** 一个事物；名字随当前理解变化，所以 30 秒后重读。读不到时为 null。 */
export function thingQuery(source: OrisumiSource, thingId: string | null | undefined) {
  return {
    queryKey: ["thing", thingId],
    queryFn: () => loadThing(source, thingId!),
    enabled: !!thingId,
    staleTime: THING_STALE,
    retry,
  };
}

export function useThing(thingId: string | null | undefined) {
  return useQuery(thingQuery(useSource(), thingId));
}

/** 按把手读取一项记录：依次当作事物、Observation、InputSet 和修订。已删除时报告 410。 */
export function useItem(handle: string) {
  const source = useSource();
  return useQuery({
    queryKey: ["item", handle],
    queryFn: async (): Promise<Item> => {
      const thing = await loadThing(source, handle);
      if (thing) return thing;
      const missing = <T>(read: Promise<T>) =>
        read.catch((error: unknown) => {
          if (error instanceof ApiError && error.status === 404) return null;
          throw error;
        });
      const found =
        (await missing(source.getObservation(handle))) ??
        (await missing(source.getInputSet(handle))) ??
        (await missing(source.getSchemaRevision(handle)));
      if (!found) throw new ApiError(404, "读不到这份记录");
      return found;
    },
    retry,
  });
}

/** 对象视图；Same-As 和否定都由后端算好。 */
export function useThingView(thingId: string | null | undefined) {
  const source = useSource();
  return useQuery({
    queryKey: ["thing-view", thingId],
    queryFn: () => source.getThing(thingId!),
    enabled: !!thingId,
    staleTime: 10_000,
    retry,
  });
}

export function useInputSet(inputSetId: string | null | undefined) {
  const source = useSource();
  return useQuery({
    queryKey: ["input-set", inputSetId],
    queryFn: () => source.getInputSet(inputSetId!),
    enabled: !!inputSetId,
    staleTime: Infinity,
    retry,
  });
}

/** 修订不会改变，读到后一直缓存。 */
export function schemaRevisionQuery(source: OrisumiSource, schemaRevisionId: string | null | undefined) {
  return {
    queryKey: ["schema-revision", schemaRevisionId],
    queryFn: () => source.getSchemaRevision(schemaRevisionId!),
    enabled: !!schemaRevisionId,
    staleTime: Infinity,
    retry,
  };
}

export function useSchemaRevision(schemaRevisionId: string | null | undefined) {
  return useQuery(schemaRevisionQuery(useSource(), schemaRevisionId));
}

/** 一项记录的引用方。 */
export function useReferrers(item: Item | null) {
  const source = useSource();
  const id = item ? handleOf(item) : null;
  return useInfiniteQuery({
    queryKey: ["referrers", id],
    queryFn: ({ pageParam }) => {
      const query = { limit: 50, view: "all" as const, ...(pageParam ? { after: pageParam } : {}) };
      return item && isThing(item)
        ? source.listThingReferrers(id!, query)
        : source.listObservationReferrers(id!, query);
    },
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.next ?? undefined,
    enabled: !!item && (isThing(item) || isObservation(item)),
    retry,
  });
}

/** 一种类型的记录：resourceName 是业务类型；things 时列的是这种类型的事物。 */
export interface ItemFilter {
  resourceName: string;
  things?: boolean;
}

export const COUNT_LIMIT = 100;

/** 一种类型的记录数，最多数到 100；更多时 more 为 true。 */
export function useItemCount(filter: ItemFilter | null) {
  const source = useSource();
  return useQuery({
    queryKey: ["items", filter, "count"],
    queryFn: async () => {
      const { things, resourceName } = filter!;
      const page = things
        ? await source.listThings({ resourceName, limit: COUNT_LIMIT }).then((p) => ({ items: p.things, next: p.next }))
        : await source
            .listObservations({ resourceName, view: "all", limit: COUNT_LIMIT })
            .then((p) => ({ items: p.observations, next: p.next }));
      return { count: page.items.length, more: page.next !== null };
    },
    enabled: !!filter,
    retry,
  });
}
