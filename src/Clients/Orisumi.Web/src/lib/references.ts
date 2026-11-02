import { isInputSet, isObservation, type FieldDefinition, type Item, type ReferenceRole } from "@/api/types";

/** 一份记录引用的目标：事物或 Observation。path 是引用的位置。 */
export interface OutgoingReference {
  path: string;
  target: "thing" | "observation";
  id: string;
  role?: ReferenceRole | undefined;
}

/**
 * 这份记录引用了什么：Observation 的 Observer、说话人、当时的描述、InputSet 的成员、替换的说法，
 * 以及 Schema 定义为 reference 的业务字段，包括 reference 数组的每一项和对象里的嵌套字段。
 */
export function outgoingReferences(
  item: Item,
  fields: Record<string, FieldDefinition> | undefined,
): OutgoingReference[] {
  if (isInputSet(item))
    return item.members.map((id, i) => ({ path: `members[${i}]`, target: "observation" as const, id }));
  if (!isObservation(item)) return [];
  const found: OutgoingReference[] = [{ path: "observerId", target: "thing", id: item.observerId }];
  if (item.speakerId) found.push({ path: "speakerId", target: "thing", id: item.speakerId });
  if (item.observerDescriptionId)
    found.push({ path: "observerDescriptionId", target: "observation", id: item.observerDescriptionId });
  item.replaces.forEach((id, i) => found.push({ path: `replaces[${i}]`, target: "observation", id }));
  return [...found, ...contentReferences(item, fields)];
}

/** Observation 的内容里引用了什么，即 Schema 定义为 reference 的业务字段。 */
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
    found.push({
      path,
      target: definition.target === "thing" ? "thing" : "observation",
      id: value,
      role: definition.role,
    });
  else if (definition.type === "array" && definition.items && Array.isArray(value))
    value.forEach((item, i) => visit(item, definition.items!, `${path}[${i}]`, found));
  else if (definition.type === "object" && definition.fields) collect(value, definition.fields, path, found);
}

/**
 * 引用位置在界面上的写法：去掉内容的根 `/content/` 和字段名末尾的 `Id`，数组下标写成方括号。
 * 例如 `/content/localApplicationId` 写成 `localApplication`，`/content/items/0/targetId` 写成 `items[0].target`。
 */
export function referenceLabel(path: string): string {
  return path
    .replace(/^\/content(?=\/|$)/, "")
    .replace(/\/(\d+)(?=\/|$)/g, "[$1]")
    .replace(/^\//, "")
    .replaceAll("/", ".")
    .replace(/Id(?=$|[.[])/g, "");
}
