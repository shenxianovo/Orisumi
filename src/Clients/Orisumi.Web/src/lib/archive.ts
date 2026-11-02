import type { Schema, Observation } from "@/api/types";
import { NAMES, RELATIONS, SAME_AS, type RelationContent, type SameAsContent } from "./common";
import { contentReferences } from "./references";
import { parseTime } from "./time";

// 档案：对象视图的陈述分到现在的样子、关系和同一。
// 分组看 Schema 声明的 role，所以接入新的 Observer 和类型后档案照样能用。

export interface ArchiveStatement {
  observation: Observation;
  schema: Schema | null;
}

/** 经由一份陈述连到的另一个事物，label 写字段或 predicate。 */
export interface ArchiveLink {
  key: string;
  thingId: string;
  label: string;
  statement: Observation;
}

export interface Archive {
  names: Observation[];
  observers: string[];
  links: ArchiveLink[];
  /** 说明它和另一个事物是同一个的陈述。 */
  sameAs: ArchiveLink[];
  state: Observation[];
  episodes: Observation[];
}

/** 字段在连线上的写法：去掉下标和末尾的 Id、Ids。 */
export function fieldLabel(path: string): string {
  return path
    .replace(/\[\d+\]/g, "")
    .replace(/Ids?$/, "")
    .replace(/\.(\w)/g, (_, char: string) => char.toUpperCase());
}

export function statementSpan(observation: Observation): number {
  const start = parseTime(observation.startAt);
  const end = parseTime(observation.endAt);
  return start !== null && end !== null && end > start ? end - start : 0;
}

/** group 是这个事物和按 Same-As 同一的事物。statements 是对象视图的 state 和 relations。 */
export function buildArchive(group: ReadonlySet<string>, statements: readonly ArchiveStatement[]): Archive {
  const archive: Archive = { names: [], observers: [], links: [], sameAs: [], state: [], episodes: [] };
  const links = new Map<string, ArchiveLink>();
  const link = (statement: Observation, target: string, label: string) => {
    const key = `${label}|${target}`;
    if (!links.has(key)) links.set(key, { key, thingId: target, label, statement });
  };

  for (const { observation, schema } of statements) {
    const resourceName = schema?.resourceName;
    if (resourceName === SAME_AS) {
      const { leftId, rightId } = observation.content as unknown as SameAsContent;
      const key = `same|${observation.observationId}`;
      archive.sameAs.push({
        key,
        thingId: group.has(leftId) ? rightId : leftId,
        label: "同一",
        statement: observation,
      });
      continue;
    }
    if (resourceName !== NAMES) archive.state.push(observation);
    if (resourceName === RELATIONS) {
      const { subjectId, objectId, predicate } = observation.content as unknown as RelationContent;
      if (group.has(subjectId)) link(observation, objectId, predicate);
      else link(observation, subjectId, `被它${predicate}`);
      continue;
    }
    for (const reference of contentReferences(observation, schema?.fields))
      if (reference.target === "thing" && !group.has(reference.id))
        link(observation, reference.id, fieldLabel(reference.path));
  }

  archive.links = [...links.values()];
  return archive;
}
