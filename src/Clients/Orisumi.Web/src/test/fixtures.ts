import type { InputSet, Observation, Schema, SchemaRevision, Thing } from "@/api/types";

let counter = 0;

/** 测试用的 UUIDv7 形状的 ID，按调用顺序递增。 */
export function id(): string {
  counter += 1;
  return `0192b056-7300-7000-8000-${counter.toString(16).padStart(12, "0")}`;
}

export function observation(overrides: Partial<Observation> = {}): Observation {
  return {
    observationId: id(),
    schemaRevisionId: "schema-v1",
    observerId: "observer",
    speakerId: null,
    observerDescriptionId: "observer-v1",
    formation: "direct",
    inputSetId: null,
    replaces: [],
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
    thingId: id(),
    schemaId: "thing-schema",
    keys: [],
    name: null,
    nameObservationId: null,
    ...overrides,
  };
}

export function inputSet(members: string[]): InputSet {
  return { inputSetId: id(), members };
}

export function schema(content: Partial<Schema> & Pick<Schema, "resourceName">): SchemaRevision {
  return {
    schemaRevisionId: id(),
    schemaId: id(),
    recordedAt: "2026-10-25T00:00:00.000Z",
    content: {
      displayName: content.resourceName,
      description: "",
      fields: {},
      ...content,
    },
  };
}
