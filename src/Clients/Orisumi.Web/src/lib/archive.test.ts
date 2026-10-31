import { describe, expect, it } from "vitest";
import type { EntitySchema, FieldDefinition, ObservationVersion } from "@/api/types";
import { observation } from "@/test/fixtures";
import { buildArchive, fieldLabel, type ArchiveStatement } from "./archive";

const reference = (): FieldDefinition => ({ type: "reference", target: "thing", description: "" });
const references = (): FieldDefinition => ({ type: "array", items: reference(), description: "" });
const text = (): FieldDefinition => ({ type: "string", description: "" });

const type = (resourceName: string, fields: Record<string, FieldDefinition>): EntitySchema => ({
  resourceName,
  displayName: resourceName,
  description: "",
  kind: "observation",
  immutable: false,
  fields,
});

const NAMES = type("names", { thingId: reference(), name: text() });
const PERIODS = type("foreground-application-periods", { localApplicationId: reference() });
const INTERACTIONS = type("assistant-interactions", { sessionId: reference(), deviceId: reference() });
const DESCRIPTIONS = type("local-application-descriptions", {
  localApplicationId: reference(),
  deviceId: reference(),
  identifierIds: references(),
});
const RELATIONS = type("relations", { subjectId: reference(), predicate: text(), objectId: reference() });
const EPISODES = type("episodes", { title: text(), sessionIds: references(), localApplicationIds: references() });

const untimed = (overrides: Partial<ObservationVersion>) => observation({ startAt: null, endAt: null, ...overrides });
const entry = (version: ObservationVersion, schema: EntitySchema): ArchiveStatement => ({ version, schema });

describe("buildArchive", () => {
  it("thingId 字段是名字，事实时间未知的陈述连到另一个事物，有事实时间的按类型收成一束", () => {
    const archive = buildArchive("app", [
      entry(untimed({ observerId: "fg", content: { thingId: "app", name: "ChatGPT" } }), NAMES),
      entry(
        untimed({
          observerId: "fg",
          content: { localApplicationId: "app", deviceId: "mac", identifierIds: ["clue-a", "clue-b"] },
        }),
        DESCRIPTIONS,
      ),
      entry(
        observation({
          observerId: "fg",
          startAt: "2026-10-31T01:00:00Z",
          endAt: "2026-10-31T01:10:00Z",
          content: { localApplicationId: "app" },
        }),
        PERIODS,
      ),
      entry(
        observation({
          observerId: "fg",
          startAt: "2026-10-31T02:00:00Z",
          endAt: "2026-10-31T02:05:00Z",
          content: { localApplicationId: "app" },
        }),
        PERIODS,
      ),
    ]);
    expect(archive.names).toHaveLength(1);
    expect(archive.observers).toEqual(["fg"]);
    expect(archive.links.map((link) => [link.label, link.thingId])).toEqual([
      ["device", "mac"],
      ["identifier", "clue-a"],
      ["identifier", "clue-b"],
    ]);
    expect(archive.bundles).toHaveLength(1);
    expect(archive.bundles[0]!.statements).toHaveLength(2);
    expect(archive.bundles[0]!.total).toBe(15 * 60_000);
  });

  it("有事实时间的陈述引用的其他事物也连起来，同一个事物只留最新的一份", () => {
    const newest = observation({ content: { sessionId: "session", deviceId: "mac" } });
    const older = observation({ content: { sessionId: "session", deviceId: "mac" } });
    const archive = buildArchive("session", [entry(newest, INTERACTIONS), entry(older, INTERACTIONS)]);
    expect(archive.links).toHaveLength(1);
    expect(archive.links[0]!.statement).toBe(newest);
  });

  it("Relation 的线上写 predicate，宾语一边写“被它”", () => {
    const relation = untimed({
      formation: "inferred",
      content: { subjectId: "codex", predicate: "继续了", objectId: "claude" },
    });
    const statements = [entry(relation, RELATIONS)];
    expect(buildArchive("codex", statements).links[0]).toMatchObject({ thingId: "claude", label: "继续了" });
    expect(buildArchive("claude", statements).links[0]).toMatchObject({ thingId: "codex", label: "被它继续了" });
  });

  it("一件事单独列出，不进束，也不把里面的其他事物连过来", () => {
    const episode = observation({ content: { title: "写 Web", sessionIds: ["a", "b"], localApplicationIds: [] } });
    const archive = buildArchive("a", [entry(episode, EPISODES)]);
    expect(archive.episodes).toEqual([episode]);
    expect(archive.bundles).toEqual([]);
    expect(archive.links).toEqual([]);
  });

  it("只在 observerId 引用它的陈述不算", () => {
    const written = untimed({ observerId: "fg", content: { localApplicationId: "app" } });
    const archive = buildArchive("fg", [entry(written, PERIODS)]);
    expect(archive).toEqual({ names: [], observers: [], links: [], bundles: [], episodes: [] });
  });
});

describe("fieldLabel", () => {
  it("去掉下标和末尾的 Id、Ids", () => {
    expect(fieldLabel("deviceId")).toBe("device");
    expect(fieldLabel("identifierIds[2]")).toBe("identifier");
  });
});
