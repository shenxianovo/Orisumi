import type { FieldDefinition, Thing } from "@/api/types";
import { interactionTitle, type InteractionContent } from "./conversations";

// 实体在界面上的名字。事物用后端取得的名字，没有名字时写识别依据的值的第一行。
// Observation 默认取第一个不是引用的文字字段；自己没有这样的字段时，按 Schema 的 resourceName 在这里登记取法。

/** 事物的名字：后端按当前理解取得的名字，没有时写识别依据的值的第一行，再没有时写类型名。 */
export function thingTitle(thing: Thing, typeName: string): string {
  return thing.name ?? thing.keys[0]?.value.split("\n")[0] ?? typeName;
}

type Content = Record<string, unknown>;

/** 接入新的 Schema、它又没有文字字段时，在这里加一行。键是 Schema 的 resourceName。 */
const BY_RESOURCE_NAME: Record<string, (content: Content) => string | undefined> = {
  // 助手交互的文字在片段里。
  "assistant-interactions": (content) => interactionTitle(content as unknown as InteractionContent),
};

/** Observation 内容的名字；返回的 key 是名字所在的字段，摘要里不再重复它。 */
export function businessTitle(
  content: unknown,
  fields: Record<string, FieldDefinition> | undefined,
  resourceName: string | null | undefined,
): { text: string; key?: string } | null {
  const values = (content ?? {}) as Content;
  const registered = resourceName ? BY_RESOURCE_NAME[resourceName]?.(values) : undefined;
  if (registered) return { text: registered };
  const definitions = fields ?? {};
  const names = [...Object.keys(definitions), ...Object.keys(values).filter((name) => !(name in definitions))];
  const key = names.find((name) => typeof values[name] === "string" && definitions[name]?.type !== "reference");
  return key ? { text: values[key] as string, key } : null;
}
