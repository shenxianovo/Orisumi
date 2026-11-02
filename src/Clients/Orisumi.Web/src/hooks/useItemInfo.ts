import { useQueries } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import {
  isInputSet,
  isObservation,
  isSchemaRevision,
  isThing,
  handleOf,
  type Item,
  type Observation,
  type Schema,
} from "@/api/types";
import { LOCAL_APPLICATIONS } from "@/lib/applications";
import { describeChanges } from "@/lib/observations";
import { contentReferences, type OutgoingReference } from "@/lib/references";
import { roleOf, ROLE_NAMES, thingColor, type Role } from "@/lib/roles";
import { clock, parseTime } from "@/lib/time";
import { businessTitle, thingTitle } from "@/lib/titles";
import { useCatalog } from "./useApplications";
import { useObservers } from "./useObservers";
import {
  observationQuery,
  schemaRevisionQuery,
  thingQuery,
  useObservation,
  useSchemaRevision,
  useThing,
} from "./queries";
import { useTypes } from "./useTypes";

/** 一项记录的类型：Observation 用它所用的修订，事物用它的类型。 */
export interface ItemType {
  content: Schema;
}

/** 一项记录在界面上的样子：角色、名字、类型名，以及事物的颜色。 */
export interface ItemInfo {
  item: Item;
  id: string;
  schema: ItemType | null;
  role: Role;
  roleName: string;
  title: string;
  typeName: string;
  /** 只有事物有颜色。 */
  color: string | null;
}

/** sourceName 是来源的名字，取实例登记的名称。 */
export function describeItem(
  item: Item,
  schema: ItemType | null,
  sourceName?: string | null,
): Omit<ItemInfo, "item" | "schema"> {
  const role = roleOf(item);
  const typeName = isThing(item)
    ? (schema?.content.displayName ?? "事物")
    : isObservation(item)
      ? (schema?.content.displayName ?? "Observation")
      : isInputSet(item)
        ? "InputSet"
        : "Schema";
  let title = typeName;
  if (isThing(item)) title = role === "source" && sourceName ? sourceName : thingTitle(item, typeName);
  else if (isObservation(item))
    title = businessTitle(item.content, schema?.content.fields, schema?.content.resourceName)?.text ?? typeName;
  else if (isInputSet(item)) title = `${item.members.length} 个输入`;
  else if (isSchemaRevision(item)) title = item.content.displayName;
  return {
    id: handleOf(item),
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

/** 按 observationId 读取并描述一份 Observation。读不到时 info 为 null，unreadable 为 true。 */
export function useObservationInfo(observationId: string | null | undefined) {
  const observation = useObservation(observationId);
  return useDescribe(observation.data, observation.isPending && !!observationId, observation.isError);
}

/** 按 thingId 读取并描述一个事物。 */
export function useThingInfo(thingId: string | null | undefined) {
  const thing = useThing(thingId);
  return useDescribe(thing.data ?? undefined, thing.isPending && !!thingId, thing.isError || thing.data === null);
}

export function useDescribe(item: Item | undefined, pending = false, failed = false) {
  const context = useDescribeContext();
  const revision = useSchemaRevision(item && isObservation(item) ? item.schemaRevisionId : null);
  if (!item) return { info: null, pending, unreadable: failed };
  return { info: describeIn(context, item, revision.data), pending: false, unreadable: false };
}

/**
 * 一份 Observation 相对它替换的那一份改了什么，引用写目标的名字，见 describeChanges。
 * 读完被替换的那一份、两边的修订和引用的目标之前为 null。被替换的那一份已删除时按新建写。
 */
export function useChanges(observation: Observation | null): string[] | null {
  const previousId = observation?.replaces[0] ?? null;
  const previous = useObservation(previousId);
  const schema = useSchemaRevision(observation?.schemaRevisionId);
  const previousSchema = useSchemaRevision(previous.data?.schemaRevisionId);
  const references = useReferenceNames([
    ...(observation ? contentReferences(observation, schema.data?.content.fields) : []),
    ...(previous.data ? contentReferences(previous.data, previousSchema.data?.content.fields) : []),
  ]);
  if (!observation) return null;
  if (previousId && previous.isPending) return null;
  if ([schema, previousSchema].some(loading) || references.pending) return null;
  return describeChanges(previous.data ?? null, observation, references.names);
}

/** 正在读取：已经启用、还没有结果。 */
function loading(query: { isPending: boolean; fetchStatus: string }): boolean {
  return query.isPending && query.fetchStatus !== "idle";
}

/**
 * 一批引用目标的名字，键是引用的 id，名字的取法与 useDescribe 相同。
 * 读完之前 pending 为 true；读不到的目标不在结果里。
 */
function useReferenceNames(references: readonly OutgoingReference[]) {
  const source = useSource();
  const context = useDescribeContext();
  const targets = [...new Map(references.map((reference) => [reference.id, reference])).values()];
  const items = useQueries({
    queries: targets.map((reference) =>
      reference.target === "thing" ? thingQuery(source, reference.id) : observationQuery(source, reference.id),
    ),
  });
  const revisions = useQueries({
    queries: items.map((result) =>
      schemaRevisionQuery(source, result.data && isObservation(result.data) ? result.data.schemaRevisionId : null),
    ),
  });
  const names = new Map<string, string>();
  targets.forEach((reference, i) => {
    const item = items[i]!.data;
    if (item) names.set(reference.id, describeIn(context, item, revisions[i]!.data).title);
  });
  const pending = items.some((result, i) => loading(result) || loading(revisions[i]!));
  return { names, pending };
}

function useDescribeContext() {
  return { types: useTypes().data, observers: useObservers().data, catalog: useCatalog().data };
}

/** 事物的类型来自类型索引，Observation 用它所用的那一条修订。本机应用归到产品后，用产品的名字。 */
function describeIn(
  { types, observers, catalog }: ReturnType<typeof useDescribeContext>,
  item: Item,
  revision: ItemType | null | undefined,
): ItemInfo {
  const schema = isThing(item) ? (types?.bySchemaId.get(item.schemaId) ?? null) : (revision ?? null);
  const source = isThing(item) ? observers?.get(item.thingId)?.identity.typeName : null;
  const described = describeItem(item, schema, source);
  const product =
    isThing(item) && schema?.content.resourceName === LOCAL_APPLICATIONS ? catalog?.productOf(item) : undefined;
  if (product) Object.assign(described, { title: product.name, color: thingColor(product.name) });
  return { item, schema, ...described };
}
