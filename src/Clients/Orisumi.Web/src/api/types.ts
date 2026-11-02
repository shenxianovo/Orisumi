// HTTP 契约的类型，按 src/Docs/Orisumi.Docs/content/docs/api/openapi.json 手写。

export type Formation = "direct" | "stated" | "computed" | "extracted" | "inferred";

/** InputSet 是一个值：成员是 Observation 的 id。 */
export interface InputSet {
  inputSetId: string;
  members: string[];
}

export type ReferenceRole = "about" | "part" | "mention";

export interface FieldDefinition {
  description: string;
  type: "string" | "boolean" | "integer" | "number" | "reference" | "binary" | "object" | "array";
  required?: boolean;
  nullable?: boolean;
  /** `reference` 字段指向事物还是 Observation。 */
  target?: "thing" | "observation";
  /** 引用事物的字段声明角色。 */
  role?: ReferenceRole;
  searchable?: boolean;
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

/** 一种 Observation 类型的声明。 */
export interface Schema {
  displayName: string;
  description: string;
  resourceName: string;
  fields: Record<string, FieldDefinition>;
  stateFields?: string[];
  claimFields?: string[];
}

export interface SchemaRevision {
  schemaRevisionId: string;
  schemaId: string;
  recordedAt: string;
  content: Schema;
}

/** 一种事物类型的声明：只有名称和说明，没有字段。 */
export interface ThingType {
  schemaId: string;
  displayName: string;
  description: string;
  resourceName: string;
}

/** Observation 的业务字段。 */
export type Fields = Record<string, unknown>;

export interface DeniedRange {
  startAt: string;
  endAt: string;
}

/** 一份不可变的陈述。deniedRanges 和 basisDenied 只在默认视图的结果中出现。 */
export interface Observation<C = Fields> {
  observationId: string;
  schemaRevisionId: string;
  observerId: string;
  speakerId: string | null;
  observerDescriptionId: string | null;
  formation: Formation;
  inputSetId: string | null;
  replaces: string[];
  startAt: string | null;
  endAt: string | null;
  timeZone: string | null;
  recordedAt: string;
  content: C;
  deniedRanges?: DeniedRange[];
  basisDenied?: boolean;
}

export interface RecognitionKey {
  scope: string | null;
  scheme: string;
  value: string;
}

/** 事物只有身份：类型、识别依据，以及后端按当前理解取得的名字。 */
export interface Thing {
  thingId: string;
  schemaId: string;
  keys: RecognitionKey[];
  name: string | null;
  /** name 所在的 Name，用它查到谁这样称呼这个事物。 */
  nameObservationId: string | null;
}

/** 界面上的一项记录：事物、Observation、InputSet 或一条 SchemaRevision。 */
export type Item = Thing | Observation | InputSet | SchemaRevision;

export function isThing(item: Item): item is Thing {
  return "thingId" in item;
}

export function isObservation(item: Item): item is Observation {
  return "observationId" in item;
}

export function isInputSet(item: Item): item is InputSet {
  return "members" in item;
}

export function isSchemaRevision(item: Item): item is SchemaRevision {
  return "schemaRevisionId" in item && !("observationId" in item);
}

/** 一项记录在界面上的把手。 */
export function handleOf(item: Item): string {
  if (isThing(item)) return item.thingId;
  if (isObservation(item)) return item.observationId;
  return isInputSet(item) ? item.inputSetId : item.schemaRevisionId;
}

/** 对象视图的一项：发生过的事的计数和分页入口。 */
export interface ThingActivity {
  resourceName: string;
  role: ReferenceRole;
  count: number;
  href: string;
}

export interface ThingView {
  thing: Thing;
  sameAsThingIds: string[];
  state: Observation[];
  relations: Observation[];
  activity: ThingActivity[];
}

export interface ObservationReadResults {
  results: { observationId: string; status: "found" | "deleted" | "not_found"; observation?: Observation }[];
}

export interface ThingReadResults {
  results: { thingId: string; status: "found" | "deleted" | "not_found"; thing?: Thing }[];
}

export interface ObservationPage {
  observations: Observation[];
  next: string | null;
}

export interface ThingPage {
  things: Thing[];
  next: string | null;
}

export interface SchemaRevisionPage {
  schemaRevisions: SchemaRevision[];
  next: string | null;
}

export interface ReferenceLocation {
  path: string;
  targetId: string;
}

export interface Referrer {
  observation: Observation;
  references: ReferenceLocation[];
}

export interface ReferrerPage {
  referrers: Referrer[];
  next: string | null;
}

export interface DeleteResult {
  deleted: string[];
  dependents: string[];
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  rule?: string;
  code?: string;
}

export type View = "current" | "all";

export interface ListThingsQuery {
  resourceName?: string;
  limit?: number;
  after?: string;
}

export interface ListObservationsQuery {
  from?: string;
  to?: string;
  thingId?: string;
  role?: ReferenceRole;
  observerId?: string;
  resourceName?: string;
  formation?: Formation;
  view?: View;
  order?: "asc" | "desc";
  limit?: number;
  after?: string;
}

export interface SearchQuery {
  q: string;
  resourceName?: string;
  thingId?: string;
  view?: View;
  order?: "asc" | "desc";
  limit?: number;
  after?: string;
}

export interface ListReferrersQuery {
  view?: View;
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

export type WriteResult =
  | { observation: Observation; changed: boolean; replaced: string[] }
  | { skipped: true; changed: false; replaced: string[] }
  | { error: ProblemDetails };

export interface WriteResponse {
  results: WriteResult[];
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
  | { type: "saved"; observationIds: string[] }
  | { type: "failed"; detail: string };
