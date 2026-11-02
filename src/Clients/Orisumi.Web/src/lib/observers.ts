import { categoryOf, type CategoryKey } from "./categories";

/** 一个 Observer，或者一组读不到 Observer 的 Observation。 */
export interface ObserverIdentity {
  key: string;
  thingId: string | null;
  typeName: string | null;
  /** 程序实例的安装包，或者资源名 people。 */
  source: string | null;
  group: CategoryKey;
}

export const UNREADABLE = "不可读取";
export const UNREADABLE_KEY = "unreadable";

export const UNREADABLE_IDENTITY: ObserverIdentity = {
  key: UNREADABLE_KEY,
  thingId: null,
  typeName: null,
  source: null,
  group: "other",
};

export function observerIdentity(thingId: string, typeName: string | null, source: string | null): ObserverIdentity {
  return { key: thingId, thingId, typeName, source, group: categoryOf(source) };
}

/** 名称取类型名；同名的 Observer 有多个时，加 thingId 的末 4 位。 */
export function nameObservers(identities: ObserverIdentity[]): Map<string, string> {
  const counts = new Map<string, number>();
  for (const identity of identities) {
    if (identity.thingId) counts.set(baseName(identity), (counts.get(baseName(identity)) ?? 0) + 1);
  }
  const names = new Map<string, string>();
  for (const identity of identities) {
    if (!identity.thingId) {
      names.set(identity.key, UNREADABLE);
      continue;
    }
    const name = baseName(identity);
    names.set(identity.key, (counts.get(name) ?? 0) > 1 ? `${name} ${identity.thingId.slice(-4)}` : name);
  }
  return names;
}

function baseName(identity: ObserverIdentity): string {
  return identity.typeName ?? "Observer";
}
