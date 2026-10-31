import { useQueries } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import {
  isEntitySchema,
  isInputSet,
  isObservation,
  isThing,
  type EntitySchema,
  type Item,
  type Version,
} from "@/api/types";
import { roleOf, ROLE_NAMES, thingColor, type Role } from "@/lib/roles";
import { clock, parseTime } from "@/lib/time";
import { businessTitle, thingTitle } from "@/lib/titles";
import { CATEGORY_NAMES, describeChanges } from "@/lib/versions";
import { LOCAL_APPLICATIONS } from "@/lib/applications";
import { contentReferences, type OutgoingReference } from "@/lib/references";
import { useCatalog } from "./useApplications";
import { useObservers } from "./useObservers";
import { thingQuery, useThing, useVersion, versionQuery } from "./queries";
import { useTypes } from "./useTypes";

/** 一个实体在界面上的样子：角色、名字、类型名，以及事物的颜色。 */
export interface EntityInfo {
  item: Item;
  entityId: string;
  schema: Version<EntitySchema> | null;
  role: Role;
  roleName: string;
  title: string;
  typeName: string;
  /** 只有事物有颜色。 */
  color: string | null;
}

/** sourceName 是来源的名字，取它的描述的类型名。 */
export function describeItem(
  item: Item,
  schema: Version<EntitySchema> | null,
  sourceName?: string | null,
): Omit<EntityInfo, "item" | "schema"> {
  const role = roleOf(item);
  const typeName = isThing(item)
    ? (schema?.content.displayName ?? "事物")
    : isObservation(item)
      ? (schema?.content.displayName ?? "Observation")
      : CATEGORY_NAMES[item.category];
  let title = typeName;
  if (isThing(item)) title = role === "source" && sourceName ? sourceName : thingTitle(item, typeName);
  else if (isObservation(item))
    title = businessTitle(item.content, schema?.content.fields, schema?.content.resourceName)?.text ?? typeName;
  else if (isInputSet(item)) title = `${item.content.members.length} 个输入`;
  else if (isEntitySchema(item)) title = item.content.displayName;
  return {
    entityId: item.entityId,
    role,
    roleName: ROLE_NAMES[role],
    title,
    typeName,
    color: role === "thing" ? thingColor(title) : null,
  };
}

export function timeLabel(startAt: string | null, endAt: string | null): string {
  const start = parseTime(startAt);
  const end = parseTime(endAt);
  if (start !== null && end !== null) return start === end ? clock(start) : `${clock(start)}–${clock(end)}`;
  if (start !== null || end !== null) return clock((start ?? end)!);
  return "时间未知";
}

/** 按 VersionId 读取并描述一个版本。读不到时 info 为 null，unreadable 为 true。 */
export function useEntityInfo(versionId: string | null | undefined) {
  const version = useVersion(versionId);
  return useDescribe(version.data, version.isPending && !!versionId, version.isError);
}

/** 按 EntityId 读取并描述一个事物。 */
export function useThingInfo(entityId: string | null | undefined) {
  const thing = useThing(entityId);
  return useDescribe(thing.data ?? undefined, thing.isPending && !!entityId, thing.isError || thing.data === null);
}

export function useDescribe(item: Item | undefined, pending = false, failed = false) {
  const context = useDescribeContext();
  const versioned = useVersion(item && isObservation(item) ? item.schemaVersionId : null);
  if (!item) return { info: null, pending, unreadable: failed };
  return { info: describeIn(context, item, versioned.data), pending: false, unreadable: false };
}

/**
 * 一份 Observation 相对前一版改了什么，引用写目标的名字，见 describeChanges。
 * 读完前一版、两版的 Schema 和引用的目标之前为 null。
 */
export function useChanges(observation: Item | null): string[] | null {
  const previous = useVersion(observation && isObservation(observation) ? observation.previousVersionId : null);
  const schema = useVersion(observation && isObservation(observation) ? observation.schemaVersionId : null);
  const previousSchema = useVersion(
    previous.data && isObservation(previous.data) ? previous.data.schemaVersionId : null,
  );
  const fields = (version: Item | undefined) =>
    version && isEntitySchema(version) ? version.content.fields : undefined;
  const references = useReferenceNames([
    ...(observation ? contentReferences(observation, fields(schema.data)) : []),
    ...(previous.data ? contentReferences(previous.data, fields(previousSchema.data)) : []),
  ]);
  if (!observation || !isObservation(observation)) return null;
  if (observation.previousVersionId && !previous.data) return null;
  if ([schema, previousSchema].some(loading) || references.pending) return null;
  return describeChanges(previous.data ?? null, observation, references.names);
}

/** 正在读取：已经启用、还没有结果。 */
function loading(query: { isPending: boolean; fetchStatus: string }): boolean {
  return query.isPending && query.fetchStatus !== "idle";
}

/**
 * 一批引用目标的名字，键是引用的 EntityId 或 VersionId，名字的取法与 useDescribe 相同。
 * 读完之前 pending 为 true；读不到的目标不在结果里。
 */
function useReferenceNames(references: readonly OutgoingReference[]) {
  const source = useSource();
  const context = useDescribeContext();
  const targets = [...new Map(references.map((reference) => [reference.id, reference])).values()];
  const items = useQueries({
    queries: targets.map((reference) =>
      reference.target === "thing" ? thingQuery(source, reference.id) : versionQuery(source, reference.id),
    ),
  });
  const schemas = useQueries({
    queries: items.map((result) =>
      versionQuery(source, result.data && isObservation(result.data) ? result.data.schemaVersionId : null),
    ),
  });
  const names = new Map<string, string>();
  targets.forEach((reference, i) => {
    const item = items[i]!.data;
    if (item) names.set(reference.id, describeIn(context, item, schemas[i]!.data).title);
  });
  const pending = items.some((result, i) => loading(result) || loading(schemas[i]!));
  return { names, pending };
}

function useDescribeContext() {
  return { types: useTypes().data, observers: useObservers().data, catalog: useCatalog().data };
}

/** 事物的类型来自类型索引，Observation 用它所用的那一版 Schema。本机应用归到产品后，用产品的名字。 */
function describeIn(
  { types, observers, catalog }: ReturnType<typeof useDescribeContext>,
  item: Item,
  versioned: Item | null | undefined,
): EntityInfo {
  const schema = isThing(item)
    ? (types?.bySchemaId.get(item.schemaId) ?? null)
    : versioned && isEntitySchema(versioned)
      ? versioned
      : null;
  const source = isThing(item) ? observers?.get(item.entityId)?.identity.typeName : null;
  const described = describeItem(item, schema, source);
  const product =
    isThing(item) && schema?.content.resourceName === LOCAL_APPLICATIONS ? catalog?.productOf(item) : undefined;
  if (product) Object.assign(described, { title: product.name, color: thingColor(product.name) });
  return { item, schema, ...described };
}
