// HTTP 契约的类型，按 src/Docs/Orisumi.Docs/content/docs/api/openapi.json 手写。

export type Category = "observation" | "entity_schema" | "input_set";

export type Formation = "direct" | "stated" | "computed" | "extracted" | "inferred";

export interface InputSet {
  members: string[];
}

export interface FieldDefinition {
  description: string;
  type: "string" | "boolean" | "integer" | "number" | "reference" | "binary" | "object" | "array";
  required?: boolean;
  nullable?: boolean;
  /** `reference` 字段指向事物还是版本。 */
  target?: "thing" | "version";
  fields?: Record<string, FieldDefinition>;
  items?: FieldDefinition;
}

/** `binary` 字段的值：字节按摘要单独读取。 */
export interface BinaryValue {
  digest: string;
  mediaType: string;
  size: number;
}

export function isBinaryValue(value: unknown): value is BinaryValue {
  if (typeof value !== "object" || value === null) return false;
  const { digest, mediaType, size } = value as Record<string, unknown>;
  return typeof digest === "string" && typeof mediaType === "string" && typeof size === "number";
}

export interface EntitySchema {
  displayName: string;
  description: string;
  resourceName: string;
  kind: "thing" | "observation";
  immutable: boolean;
  fields: Record<string, FieldDefinition>;
}

/** Observation 的业务字段。 */
export type Fields = Record<string, unknown>;

export interface Version<C = unknown> {
  entityId: string;
  versionId: string;
  previousVersionId: string | null;
  category: Category;
  recordedAt: string;
  content: C;
}

/** Observation 的版本：负责的 Observer、形成方式、依据和事实时间都在版本上，content 是业务字段。 */
export interface ObservationVersion<C = Fields> extends Version<C> {
  category: "observation";
  schemaVersionId: string;
  observerId: string;
  observerVersionId: string | null;
  formation: Formation;
  inputSetVersionId: string | null;
  startAt: string | null;
  endAt: string | null;
  timeZone: string | null;
}

export interface RecognitionKey {
  scope: string | null;
  scheme: string;
  value: string;
}

/** 事物只有身份：类型、识别依据，以及后端按当前理解取得的名字。 */
export interface Thing {
  entityId: string;
  category: "thing";
  schemaId: string;
  keys: RecognitionKey[];
  name: string | null;
  /** name 所在的 Name 的版本，用它查到谁这样称呼这个事物。 */
  nameVersionId: string | null;
}

/** 界面上的一个实体：事物，或者一个版本。 */
export type Item = Thing | Version;

export function isThing(item: Item): item is Thing {
  return item.category === "thing";
}

/** 实体在界面上的把手：事物用 EntityId，其余用 VersionId。 */
export function handleOf(item: Item): string {
  return isThing(item) ? item.entityId : item.versionId;
}

export interface VersionReadResults {
  results: { versionId: string; status: "found" | "deleted" | "not_found"; version?: Version }[];
}

export interface ThingReadResults {
  results: { entityId: string; status: "found" | "deleted" | "not_found"; thing?: Thing }[];
}

export interface VersionPage {
  versions: Version[];
  next: string | null;
}

export interface ThingPage {
  things: Thing[];
  next: string | null;
}

export interface VersionList {
  versions: Version[];
}

export interface ReferenceLocation {
  path: string;
  targetEntityId: string;
  /** 引用事物时为 null。 */
  targetVersionId: string | null;
}

export interface Referrer {
  version: Version;
  references: ReferenceLocation[];
}

export interface ReferrerPage {
  referrers: Referrer[];
  next: string | null;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  rule?: string;
  code?: string;
}

export interface ListEntitiesQuery {
  category?: Category;
  resourceName?: string;
  limit?: number;
  after?: string;
}

export interface ListThingsQuery {
  resourceName?: string;
  limit?: number;
  after?: string;
}

export interface ListObservationsQuery {
  from?: string;
  to?: string;
  observerId?: string;
  resourceName?: string;
  formation?: Formation;
  standing?: "all" | "current";
  order?: "asc" | "desc";
  limit?: number;
  after?: string;
}

export interface ListReferrersQuery {
  versions?: "current" | "all";
  resourceName?: string;
  path?: string;
  order?: "asc" | "desc";
  limit?: number;
  after?: string;
}

/** 一次写入：一份 Observation 的完整内容。 */
export interface Write {
  schemaId: string;
  formation: Formation;
  content: Fields;
  time?: { startAt: string | null; endAt: string | null; timeZone: string | null };
  inputs?: string[];
}

export interface WriteRequest {
  observer: { schemaId: string; key: RecognitionKey };
  writes: Write[];
}

export interface WriteResponse {
  results: ({ version: Version; changed: boolean } | { error: ProblemDetails })[];
}

/** 请求叙事 Observer 写一段时间，通常是用户所在时区的一天。 */
export interface NarrativeRequest {
  from: string;
  to: string;
  timeZone: string;
}

/** 叙事流里刚写完的一件事。 */
export interface NarrativeEpisode {
  startAt: string;
  endAt: string;
  title: string;
  summary: string;
  sessionIds: string[];
  localApplicationIds: string[];
}

export type NarrativeEvent =
  | { type: "episode"; episode: NarrativeEpisode }
  | { type: "saved"; versionIds: string[] }
  | { type: "failed"; detail: string };

export function isObservation(version: Item): version is ObservationVersion {
  return version.category === "observation";
}

export function isInputSet(version: Item): version is Version<InputSet> {
  return version.category === "input_set";
}

export function isEntitySchema(version: Item): version is Version<EntitySchema> {
  return version.category === "entity_schema";
}
