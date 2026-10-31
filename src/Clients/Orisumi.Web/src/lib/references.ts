import { isInputSet, isObservation, type FieldDefinition, type Item } from "@/api/types";

/** 一个版本引用的实体：事物写 EntityId，其余写 VersionId。path 是引用在版本里的位置。 */
export interface OutgoingReference {
  path: string;
  target: "thing" | "version";
  id: string;
}

/**
 * 这个版本引用了哪些实体：Observation 的 Observer、它当时的描述和 InputSet，以及 Schema 定义为 reference 的业务字段，
 * 包括 reference 数组的每一项和对象里的嵌套字段；InputSet 的每个输入。事物和 EntitySchema 不引用别的实体。
 */
export function outgoingReferences(
  item: Item,
  fields: Record<string, FieldDefinition> | undefined,
): OutgoingReference[] {
  if (isInputSet(item))
    return item.content.members.map((id, i) => ({ path: `members[${i}]`, target: "version" as const, id }));
  if (!isObservation(item)) return [];
  const found: OutgoingReference[] = [{ path: "observerId", target: "thing", id: item.observerId }];
  if (item.observerVersionId) found.push({ path: "observerVersionId", target: "version", id: item.observerVersionId });
  if (item.inputSetVersionId) found.push({ path: "inputSetVersionId", target: "version", id: item.inputSetVersionId });
  return [...found, ...contentReferences(item, fields)];
}

/** Observation 的内容里引用了哪些实体，即 Schema 定义为 reference 的业务字段。 */
export function contentReferences(
  item: Item,
  fields: Record<string, FieldDefinition> | undefined,
): OutgoingReference[] {
  const found: OutgoingReference[] = [];
  if (isObservation(item) && fields) collect(item.content, fields, "", found);
  return found;
}

function collect(value: unknown, fields: Record<string, FieldDefinition>, prefix: string, found: OutgoingReference[]) {
  if (!value || typeof value !== "object") return;
  const record = value as Record<string, unknown>;
  for (const [name, definition] of Object.entries(fields)) {
    visit(record[name], definition, prefix ? `${prefix}.${name}` : name, found);
  }
}

function visit(value: unknown, definition: FieldDefinition, path: string, found: OutgoingReference[]) {
  if (value === null || value === undefined) return;
  if (definition.type === "reference" && typeof value === "string")
    found.push({ path, target: definition.target === "thing" ? "thing" : "version", id: value });
  else if (definition.type === "array" && definition.items && Array.isArray(value))
    value.forEach((item, i) => visit(item, definition.items!, `${path}[${i}]`, found));
  else if (definition.type === "object" && definition.fields) collect(value, definition.fields, path, found);
}

/**
 * 引用位置在界面上的写法：去掉内容的根 `/content/` 和字段名末尾的 `Id` 或 `VersionId`，数组下标写成方括号。
 * 例如 `/content/localApplicationId` 写成 `localApplication`，`/content/items/0/targetVersionId` 写成 `items[0].target`。
 */
export function referenceLabel(path: string): string {
  return path
    .replace(/^\/content(?=\/|$)/, "")
    .replace(/\/(\d+)(?=\/|$)/g, "[$1]")
    .replace(/^\//, "")
    .replaceAll("/", ".")
    .replace(/(?:Version)?Id(?=$|[.[])/g, "");
}
