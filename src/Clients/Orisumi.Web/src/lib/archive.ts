import type { EntitySchema, ObservationVersion } from "@/api/types";
import { RELATIONS, type RelationContent } from "./common";
import { EPISODES } from "./episodes";
import { contentReferences } from "./references";
import { parseTime } from "./time";

// 档案：引用一个事物的陈述分到称呼、看到过它的 Observer、关系和经历。
// 分组看 EntitySchema 的字段和事实时间，所以接入新的 Observer 和类型后档案照样能用。

export interface ArchiveStatement {
  version: ObservationVersion;
  schema: EntitySchema | null;
}

/** 经由一份陈述连到的另一个事物，label 写字段或 predicate。 */
export interface ArchiveLink {
  key: string;
  thingId: string;
  label: string;
  statement: ObservationVersion;
}

/** 同一种类型的有事实时间的陈述。 */
export interface ArchiveBundle {
  resourceName: string;
  typeName: string;
  statements: ObservationVersion[];
  total: number;
}

export interface Archive {
  names: ObservationVersion[];
  observers: string[];
  links: ArchiveLink[];
  bundles: ArchiveBundle[];
  episodes: ObservationVersion[];
}

/** 字段在连线上的写法：去掉下标和末尾的 Id、Ids。 */
export function fieldLabel(path: string): string {
  return path
    .replace(/\[\d+\]/g, "")
    .replace(/Ids?$/, "")
    .replace(/\.(\w)/g, (_, char: string) => char.toUpperCase());
}

export function statementSpan(version: ObservationVersion): number {
  const start = parseTime(version.startAt);
  const end = parseTime(version.endAt);
  return start !== null && end !== null && end > start ? end - start : 0;
}

/** statements 按新到旧排列，同一个事物经由同一个字段只留最新的一份陈述。 */
export function buildArchive(thingId: string, statements: readonly ArchiveStatement[]): Archive {
  const archive: Archive = { names: [], observers: [], links: [], bundles: [], episodes: [] };
  const observers = new Set<string>();
  const links = new Map<string, ArchiveLink>();
  const bundles = new Map<string, ArchiveBundle>();
  const link = (statement: ObservationVersion, target: string, label: string) => {
    const key = `${label}|${target}`;
    if (!links.has(key)) links.set(key, { key, thingId: target, label, statement });
  };

  for (const { version, schema } of statements) {
    const references = contentReferences(version, schema?.fields).filter((reference) => reference.target === "thing");
    if (!references.some((reference) => reference.id === thingId)) continue;
    if (version.observerId !== thingId) observers.add(version.observerId);
    const resourceName = schema?.resourceName ?? "unknown";

    if (schema?.fields.thingId) {
      archive.names.push(version);
      continue;
    }
    if (resourceName === EPISODES) {
      archive.episodes.push(version);
      continue;
    }
    if (version.startAt || version.endAt) {
      const bundle = bundles.get(resourceName) ?? {
        resourceName,
        typeName: schema?.displayName ?? "经历",
        statements: [],
        total: 0,
      };
      bundle.statements.push(version);
      bundle.total += statementSpan(version);
      bundles.set(resourceName, bundle);
    }
    if (resourceName === RELATIONS) {
      const { subjectId, objectId, predicate } = version.content as unknown as RelationContent;
      if (subjectId === thingId) link(version, objectId, predicate);
      else link(version, subjectId, `被它${predicate}`);
      continue;
    }
    for (const reference of references)
      if (reference.id !== thingId) link(version, reference.id, fieldLabel(reference.path));
  }

  archive.observers = [...observers];
  archive.links = [...links.values()];
  archive.bundles = [...bundles.values()].sort((a, b) => b.statements.length - a.statements.length);
  return archive;
}
