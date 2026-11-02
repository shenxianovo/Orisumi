import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { isUnreadable, readAllSchemaRevisions, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import type { Item, Observation, Schema, SchemaRevision, Thing } from "@/api/types";
import { outgoingReferences } from "@/lib/references";
import type { Role } from "@/lib/roles";

/** 一种类型：事物类型或 Observation 类型。content 取最新的修订，事物类型没有字段。 */
export interface TypeEntry {
  schemaId: string;
  thingType: boolean;
  content: Schema;
}

/** 类型图的一个点：一种类型，或者 InputSet。 */
export interface TypeNode {
  key: string;
  name: string;
  role: Role;
  type: TypeEntry | null;
}

/** 一种类型的记录引用另一种类型，label 是引用所在的字段。 */
export interface TypeEdge {
  from: string;
  to: string;
  label: string;
}

export interface TypeIndex {
  nodes: TypeNode[];
  edges: TypeEdge[];
  byResourceName: ReadonlyMap<string, TypeEntry>;
  /** 键是 SchemaId。事物按它找到自己的类型。 */
  bySchemaId: ReadonlyMap<string, TypeEntry>;
}

/** Observation 不会改变：经过查询缓存读取，和 useObservation 共用结果。 */
export function readObservation(
  client: QueryClient,
  source: OrisumiSource,
  observationId: string,
): Promise<Observation | null> {
  return client
    .fetchQuery({
      queryKey: ["observation", observationId],
      queryFn: () => source.getObservation(observationId),
      staleTime: Infinity,
    })
    .catch((error: unknown) => {
      if (isUnreadable(error)) return null;
      throw error;
    });
}

/** 修订不会改变，经过查询缓存读取。 */
export function readSchemaRevision(client: QueryClient, source: OrisumiSource, schemaRevisionId: string) {
  return client.fetchQuery({
    queryKey: ["schema-revision", schemaRevisionId],
    queryFn: () => source.getSchemaRevision(schemaRevisionId),
    staleTime: Infinity,
  });
}

/** 事物的名字随当前理解变化，缓存 30 秒。 */
export const THING_STALE = 30_000;

/** 一批事物，一次请求最多 256 个；读到的放进查询缓存，读不到的为 null。 */
export async function readThings(
  client: QueryClient,
  source: OrisumiSource,
  thingIds: readonly string[],
): Promise<Map<string, Thing | null>> {
  const found = new Map<string, Thing | null>();
  const missing: string[] = [];
  for (const thingId of new Set(thingIds)) {
    const state = client.getQueryState<Thing | null>(["thing", thingId]);
    if (state?.status === "success" && Date.now() - state.dataUpdatedAt < THING_STALE) found.set(thingId, state.data!);
    else missing.push(thingId);
  }
  for (let i = 0; i < missing.length; i += 256) {
    for (const result of (await source.readThings(missing.slice(i, i + 256))).results) {
      const thing = result.thing ?? null;
      client.setQueryData(["thing", result.thingId], thing);
      found.set(result.thingId, thing);
    }
  }
  return found;
}

export async function readThing(client: QueryClient, source: OrisumiSource, thingId: string): Promise<Thing | null> {
  return (await readThings(client, source, [thingId])).get(thingId) ?? null;
}

/** 按把手读取：先当作事物，再当作 Observation，最后当作 InputSet。 */
export async function readItem(client: QueryClient, source: OrisumiSource, handle: string): Promise<Item | null> {
  const found = (await readThing(client, source, handle)) ?? (await readObservation(client, source, handle));
  if (found) return found;
  return client
    .fetchQuery({ queryKey: ["input-set", handle], queryFn: () => source.getInputSet(handle), staleTime: Infinity })
    .catch((error: unknown) => {
      if (isUnreadable(error)) return null;
      throw error;
    });
}

/** 同时最多 limit 个请求。 */
export async function pool<T, R>(items: readonly T[], limit: number, run: (item: T) => Promise<R>): Promise<R[]> {
  const results: R[] = new Array(items.length);
  let next = 0;
  async function worker() {
    while (next < items.length) {
      const i = next++;
      results[i] = await run(items[i]!);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

/** Observation 所用的那一条修订。 */
export async function schemaOf(
  client: QueryClient,
  source: OrisumiSource,
  observation: Observation,
): Promise<SchemaRevision | null> {
  return readSchemaRevision(client, source, observation.schemaRevisionId).catch((error: unknown) => {
    if (isUnreadable(error)) return null;
    throw error;
  });
}

/** 类型的角色：程序实例是来源；Observation 的类型看样本有没有事实时间。 */
function typeRole(type: TypeEntry, timed: boolean): Role {
  if (type.thingType) return type.content.resourceName === "program-instances" ? "source" : "thing";
  return timed ? "experience" : "statement";
}

/**
 * 全部类型，以及类型图：每种 Observation 的类型取一份样本，读出它引用的类型，
 * 由此知道类型之间经由哪个字段引用，以及这种类型的陈述有没有事实时间。
 */
async function buildIndex(client: QueryClient, source: OrisumiSource): Promise<TypeIndex> {
  const [revisions, { thingTypes }] = await Promise.all([
    readAllSchemaRevisions((after) => source.listSchemaRevisions({ limit: 1000, ...(after ? { after } : {}) })),
    source.listThingTypes(),
  ]);
  const entries = new Map<string, TypeEntry>();
  // 修订按 id 升序，同一种类型留下最新的一条。
  for (const revision of revisions)
    entries.set(revision.schemaId, { schemaId: revision.schemaId, thingType: false, content: revision.content });
  for (const { schemaId, displayName, description, resourceName } of thingTypes)
    entries.set(schemaId, {
      schemaId,
      thingType: true,
      content: { displayName, description, resourceName, fields: {} },
    });
  const types = [...entries.values()];
  const byResourceName = new Map(types.map((type) => [type.content.resourceName, type]));
  const timed = new Set<string>();
  const edges = new Map<string, TypeEdge>();
  const edge = (from: string, to: string, label: string) => edges.set(`${from}>${to}>${label}`, { from, to, label });

  async function typeOf(target: "thing" | "observation", id: string): Promise<string | null> {
    if (target === "thing") {
      const thing = await readThing(client, source, id);
      return thing ? (entries.get(thing.schemaId)?.content.resourceName ?? null) : null;
    }
    const observation = await readObservation(client, source, id);
    if (!observation) return null;
    return (await schemaOf(client, source, observation))?.content.resourceName ?? null;
  }

  await pool(
    types.filter((type) => !type.thingType),
    6,
    async (type) => {
      const resourceName = type.content.resourceName;
      const sample = (await source.listObservations({ resourceName, order: "desc", limit: 1 })).observations[0];
      if (!sample) return;
      if (sample.startAt || sample.endAt) timed.add(resourceName);
      for (const reference of outgoingReferences(sample, type.content.fields)) {
        const target = await typeOf(reference.target, reference.id);
        if (target) edge(resourceName, target, reference.path.replace(/\[\d+\]/g, "[]"));
      }
    },
  );

  const nodes: TypeNode[] = types.map((type) => ({
    key: type.content.resourceName,
    name: type.content.displayName,
    role: typeRole(type, timed.has(type.content.resourceName)),
    type,
  }));
  return { nodes, edges: [...edges.values()], byResourceName, bySchemaId: entries };
}

export function useTypes() {
  const source = useSource();
  const client = useQueryClient();
  return useQuery({
    queryKey: ["types"],
    queryFn: () => buildIndex(client, source),
    staleTime: 5 * 60_000,
  });
}
