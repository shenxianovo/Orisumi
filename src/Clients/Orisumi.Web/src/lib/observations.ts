import type { Observation } from "@/api/types";

/** 相对被替换的那一份变化的字段名。 */
export function changedFields(previous: Observation | undefined, current: Observation): string[] {
  if (!previous) return [];
  const before = asRecord(previous.content);
  const after = asRecord(current.content);
  const keys = [...new Set([...Object.keys(after), ...Object.keys(before)])];
  return keys.filter((key) => JSON.stringify(before[key]) !== JSON.stringify(after[key]));
}

/**
 * 相对被替换的那一份改了什么，每处一句：数组字段写加入和移出的元素，取值是标量的写新的值，其余写“改了”。
 * 例如 identifiers 多了一条线索时是“加入 macos.bundle-id com.apple.Preview”。
 * 没有替换时是“新建”，后面是数组字段里的元素。
 * names 是引用目标的名字，键是引用的 id；引用写成目标的名字，不在其中的写原值。
 */
export function describeChanges(
  previous: Observation | null,
  current: Observation,
  names: ReadonlyMap<string, string>,
): string[] {
  const plain = (value: unknown) => written(value, names);
  const after = asRecord(current.content);
  if (!previous)
    return [
      "新建",
      ...Object.values(after)
        .filter(Array.isArray)
        .flatMap((list) => list.map((item) => `加入 ${plain(item)}`)),
    ];
  const before = asRecord(previous.content);
  const changes = changedFields(previous, current).flatMap((key) => {
    const was = before[key];
    const now = after[key];
    if (Array.isArray(was) && Array.isArray(now)) {
      const has = (list: unknown[], item: unknown) =>
        list.some((other) => JSON.stringify(other) === JSON.stringify(item));
      return [
        ...now.filter((item) => !has(was, item)).map((item) => `加入 ${plain(item)}`),
        ...was.filter((item) => !has(now, item)).map((item) => `移出 ${plain(item)}`),
      ];
    }
    if (now === undefined) return [`去掉 ${key}`];
    if (now === null || typeof now !== "object") return [`${key} 改为 ${plain(now)}`];
    return [`${key} 改了`];
  });
  return changes.length ? changes : ["内容相同"];
}

/** 一个值的简短写法：引用写目标的名字；对象按键名的顺序写出它的标量值，用空格隔开。 */
function written(value: unknown, names: ReadonlyMap<string, string>): string {
  if (value === null) return "null";
  if (typeof value === "string") return names.get(value) ?? value;
  if (typeof value !== "object") return String(value);
  return Object.entries(value)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([, item]) => item)
    .filter((item) => item === null || typeof item !== "object")
    .map((item) => written(item, names))
    .join(" ");
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
}
