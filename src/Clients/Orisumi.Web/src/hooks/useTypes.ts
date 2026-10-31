import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { isUnreadable, readAllPages, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isEntitySchema, isObservation, type EntitySchema, type Item, type Thing, type Version } from "@/api/types";
import { outgoingReferences } from "@/lib/references";
import type { Role } from "@/lib/roles";

/** 类型图的一个点：一个业务类型，或者 InputSet。 */
export interface TypeNode {
  key: string;
  name: string;
  role: Role;
  schema: Version<EntitySchema> | null;
}

/** 一种类型的实体引用另一种类型，label 是引用所在的字段。 */
export interface TypeEdge {
  from: string;
  to: string;
  label: string;
}

export interface TypeIndex {
  nodes: TypeNode[];
  edges: TypeEdge[];
  byResourceName: ReadonlyMap<string, Version<EntitySchema>>;
  /** 每个 EntitySchema 的当前版本，键是 SchemaId。事物按它找到自己的类型。 */
  bySchemaId: ReadonlyMap<string, Version<EntitySchema>>;
}

/** 版本不会改变：经过查询缓存读取，和 useVersion 共用结果。 */
export function readVersion(client: QueryClient, source: OrisumiSource, versionId: string): Promise<Version | null> {
  return client
    .fetchQuery({ queryKey: ["version", versionId], queryFn: () => source.getVersion(versionId), staleTime: Infinity })
    .catch((error: unknown) => {
      if (isUnreadable(error)) return null;
      throw error;
    });
}

/** 事物的名字随当前理解变化，缓存 30 秒。 */
export const THING_STALE = 30_000;

/** 一批事物，一次请求最多 256 个；读到的放进查询缓存，读不到的为 null。 */
export async function readThings(
  client: QueryClient,
  source: OrisumiSource,
  entityIds: readonly string[],
): Promise<Map<string, Thing | null>> {
  const found = new Map<string, Thing | null>();
  const missing: string[] = [];
  for (const entityId of new Set(entityIds)) {
    const state = client.getQueryState<Thing | null>(["thing", entityId]);
    if (state?.status === "success" && Date.now() - state.dataUpdatedAt < THING_STALE) found.set(entityId, state.data!);
    else missing.push(entityId);
  }
  for (let i = 0; i < missing.length; i += 256) {
    for (const result of (await source.readThings(missing.slice(i, i + 256))).results) {
      const thing = result.thing ?? null;
      client.setQueryData(["thing", result.entityId], thing);
      found.set(result.entityId, thing);
    }
  }
  return found;
}

export async function readThing(client: QueryClient, source: OrisumiSource, entityId: string): Promise<Thing | null> {
  return (await readThings(client, source, [entityId])).get(entityId) ?? null;
}

/** 按把手读取：先当作事物的 EntityId，读不到再当作 VersionId。 */
export async function readItem(client: QueryClient, source: OrisumiSource, handle: string): Promise<Item | null> {
  return (await readThing(client, source, handle)) ?? (await readVersion(client, source, handle));
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

/** 实体所用的 EntitySchema：事物用它的类型的当前版本，Observation 用它所用的那一版。 */
export async function schemaOf(
  client: QueryClient,
  source: OrisumiSource,
  index: TypeIndex,
  item: Item,
): Promise<Version<EntitySchema> | null> {
  if (item.category === "thing") return index.bySchemaId.get(item.schemaId) ?? null;
  if (!isObservation(item)) return null;
  const schema = await readVersion(client, source, item.schemaVersionId);
  return schema && isEntitySchema(schema) ? schema : null;
}

/** 类型的角色：程序实例是来源；Observation 的类型看样本有没有事实时间。 */
function typeRole(schema: EntitySchema, timed: boolean): Role {
  if (schema.kind === "thing") return schema.resourceName === "program-instances" ? "source" : "thing";
  return timed ? "experience" : "statement";
}

/**
 * 全部 EntitySchema，以及类型图：每种 Observation 的类型取一份样本，读出它引用的类型，
 * 由此知道类型之间经由哪个字段引用，以及这种类型的陈述有没有事实时间。
 */
async function buildIndex(client: QueryClient, source: OrisumiSource): Promise<TypeIndex> {
  const schemas = (await readAllPages((after) =>
    source.listEntities({ category: "entity_schema", limit: 1000, ...(after ? { after } : {}) }),
  )) as Version<EntitySchema>[];
  const byResourceName = new Map(schemas.map((s) => [s.content.resourceName, s]));
  const bySchemaId = new Map(schemas.map((s) => [s.entityId, s]));
  const index: TypeIndex = { byResourceName, bySchemaId, nodes: [], edges: [] };
  const timed = new Set<string>();
  const edges = new Map<string, TypeEdge>();
  const edge = (from: string, to: string, label: string) => edges.set(`${from}>${to}>${label}`, { from, to, label });

  async function typeOf(target: "thing" | "version", id: string): Promise<string | null> {
    if (target === "thing") {
      const thing = await readThing(client, source, id);
      return thing ? (bySchemaId.get(thing.schemaId)?.content.resourceName ?? null) : null;
    }
    const version = await readVersion(client, source, id);
    if (!version) return null;
    if (!isObservation(version)) return version.category;
    return (await schemaOf(client, source, index, version))?.content.resourceName ?? null;
  }

  await pool(
    schemas.filter((schema) => schema.content.kind === "observation"),
    6,
    async (schema) => {
      const resourceName = schema.content.resourceName;
      const sample = (await source.listObservations({ resourceName, order: "desc", limit: 1 })).versions[0];
      if (!sample || !isObservation(sample)) return;
      if (sample.startAt || sample.endAt) timed.add(resourceName);
      for (const reference of outgoingReferences(sample, schema.content.fields)) {
        const target = await typeOf(reference.target, reference.id);
        if (target && target !== "entity_schema") edge(resourceName, target, reference.path.replace(/\[\d+\]/g, "[]"));
      }
    },
  );

  const nodes: TypeNode[] = [
    { key: "input_set", name: "InputSet", role: "evidence", schema: null },
    ...schemas.map((schema) => ({
      key: schema.content.resourceName,
      name: schema.content.displayName,
      role: typeRole(schema.content, timed.has(schema.content.resourceName)),
      schema,
    })),
  ];
  return { nodes, edges: [...edges.values()], byResourceName, bySchemaId };
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
