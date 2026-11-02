import { describe, expect, it } from "vitest";
import type { Schema, FieldDefinition, Observation } from "@/api/types";
import { observation } from "@/test/fixtures";
import { buildArchive, fieldLabel, type ArchiveStatement } from "./archive";

const reference = (): FieldDefinition => ({ type: "reference", target: "thing", description: "" });
const references = (): FieldDefinition => ({ type: "array", items: reference(), description: "" });
const text = (): FieldDefinition => ({ type: "string", description: "" });

const type = (resourceName: string, fields: Record<string, FieldDefinition>): Schema => ({
  resourceName,
  displayName: resourceName,
  description: "",
  fields,
});

const NAMES = type("names", { thingId: reference(), name: text() });
const DESCRIPTIONS = type("local-application-descriptions", {
  localApplicationId: reference(),
  deviceId: reference(),
  identifierIds: references(),
});
const RELATIONS = type("relations", { subjectId: reference(), predicate: text(), objectId: reference() });
const SAME_AS = type("same-as", { leftId: reference(), rightId: reference() });

const untimed = (overrides: Partial<Observation>) => observation({ startAt: null, endAt: null, ...overrides });
const entry = (observation: Observation, schema: Schema): ArchiveStatement => ({ observation, schema });

describe("buildArchive", () => {
  it("名字和同一不进现在的样子，状态引用的其他事物连到右边", () => {
    const name = untimed({ observerId: "fg", content: { thingId: "app", name: "ChatGPT" } });
    const description = untimed({
      observerId: "fg",
      content: { localApplicationId: "app", deviceId: "mac", identifierIds: ["clue-a", "clue-b"] },
    });
    const archive = buildArchive(new Set(["app"]), [entry(name, NAMES), entry(description, DESCRIPTIONS)]);
    expect(archive.state).toEqual([description]);
    expect(archive.links.map((link) => [link.label, link.thingId])).toEqual([
      ["device", "mac"],
      ["identifier", "clue-a"],
      ["identifier", "clue-b"],
    ]);
  });

  it("同一个事物经由同一个字段只连一次", () => {
    const content = { localApplicationId: "app", deviceId: "mac", identifierIds: [] };
    const newest = untimed({ content });
    const older = untimed({ content });
    const archive = buildArchive(new Set(["app"]), [entry(newest, DESCRIPTIONS), entry(older, DESCRIPTIONS)]);
    expect(archive.links).toHaveLength(1);
    expect(archive.links[0]!.statement).toBe(newest);
  });

  it("Relation 的线上写 predicate，宾语一边写“被它”", () => {
    const relation = untimed({
      formation: "inferred",
      content: { subjectId: "codex", predicate: "继续了", objectId: "claude" },
    });
    const statements = [entry(relation, RELATIONS)];
    expect(buildArchive(new Set(["codex"]), statements).links[0]).toMatchObject({ thingId: "claude", label: "继续了" });
    expect(buildArchive(new Set(["claude"]), statements).links[0]).toMatchObject({
      thingId: "codex",
      label: "被它继续了",
    });
  });

  it("同一单独列出，连到不在这一组的那个事物", () => {
    const same = untimed({ content: { leftId: "old", rightId: "new" } });
    const archive = buildArchive(new Set(["new"]), [entry(same, SAME_AS)]);
    expect(archive.sameAs[0]).toMatchObject({ thingId: "old", statement: same });
    expect(archive.state).toEqual([]);
    expect(archive.links).toEqual([]);
  });

  it("按 Same-As 同一的事物不算另一个事物", () => {
    const content = { localApplicationId: "app", deviceId: "mac", identifierIds: [] };
    const archive = buildArchive(new Set(["app", "mac"]), [entry(untimed({ content }), DESCRIPTIONS)]);
    expect(archive.links).toEqual([]);
  });
});

describe("fieldLabel", () => {
  it("去掉下标和末尾的 Id、Ids", () => {
    expect(fieldLabel("deviceId")).toBe("device");
    expect(fieldLabel("identifierIds[2]")).toBe("identifier");
  });
});
