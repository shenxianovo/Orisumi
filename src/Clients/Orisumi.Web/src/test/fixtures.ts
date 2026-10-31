import type { EntitySchema, InputSet, ObservationVersion, Thing, Version } from "@/api/types";

let counter = 0;

/** 测试用的 UUIDv7 形状的 ID，按调用顺序递增。 */
export function id(): string {
  counter += 1;
  return `0192b056-7300-7000-8000-${counter.toString(16).padStart(12, "0")}`;
}

export function observation(overrides: Partial<ObservationVersion> = {}): ObservationVersion {
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "observation",
    schemaVersionId: "schema-v1",
    observerId: "observer",
    observerVersionId: "observer-v1",
    formation: "direct",
    inputSetVersionId: null,
    startAt: "2026-10-25T05:00:00.000Z",
    endAt: "2026-10-25T06:00:00.000Z",
    timeZone: "Asia/Shanghai",
    recordedAt: "2026-10-25T06:00:00.000Z",
    content: {},
    ...overrides,
  };
}

export function thing(overrides: Partial<Thing> = {}): Thing {
  return {
    entityId: id(),
    category: "thing",
    schemaId: "thing-schema",
    keys: [],
    name: null,
    nameVersionId: null,
    ...overrides,
  };
}

export function inputSet(members: string[]): Version<InputSet> {
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "input_set",
    recordedAt: "2026-10-25T06:00:00.000Z",
    content: { members },
  };
}

export function schema(content: Partial<EntitySchema> & Pick<EntitySchema, "resourceName">): Version<EntitySchema> {
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "entity_schema",
    recordedAt: "2026-10-25T00:00:00.000Z",
    content: {
      displayName: content.resourceName,
      description: "",
      kind: "observation",
      immutable: false,
      fields: {},
      ...content,
    },
  };
}
