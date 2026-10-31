import type { Category } from "@/api/types";
import type { EntityFilter } from "@/hooks/queries";

/** Core 的一类实体：Observation、InputSet 或 EntitySchema。 */
export interface CoreKind {
  key: Category;
  name: string;
  hint: string;
  tint: string;
  filter: EntityFilter;
}

export const CORE_KINDS: readonly CoreKind[] = [
  {
    key: "observation",
    name: "Observation",
    hint: "Observer 写下的每一份陈述，带着形成方式、输入和事实时间",
    tint: "#5f86a8",
    filter: { category: "observation" },
  },
  {
    key: "input_set",
    name: "InputSet",
    hint: "一份推断所依据的一组输入版本",
    tint: "#d49a4a",
    filter: { category: "input_set" },
  },
  {
    key: "entity_schema",
    name: "EntitySchema",
    hint: "登记过的业务类型，规定字段和引用",
    tint: "#888780",
    filter: { category: "entity_schema" },
  },
];

export function coreKind(category: Category): CoreKind | undefined {
  return CORE_KINDS.find((kind) => kind.key === category);
}
