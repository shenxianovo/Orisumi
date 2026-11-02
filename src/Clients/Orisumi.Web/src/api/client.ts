import { parseServerEvents } from "@/lib/sse";
import type {
  DeleteResult,
  InputSet,
  ListObservationsQuery,
  ListReferrersQuery,
  ListThingsQuery,
  NarrativeEpisode,
  NarrativeEvent,
  NarrativeRequest,
  Observation,
  ObservationPage,
  ObservationReadResults,
  ProblemDetails,
  ReferrerPage,
  SchemaRevision,
  SchemaRevisionPage,
  SearchQuery,
  Thing,
  ThingPage,
  ThingReadResults,
  ThingType,
  ThingView,
  WriteRequest,
  WriteResponse,
} from "./types";

/** 控制台读写数据的接口，HTTP 实现读写后端。 */
export interface OrisumiSource {
  getObservation(observationId: string): Promise<Observation>;
  /** 一次读取最多 256 份。 */
  readObservations(observationIds: string[]): Promise<ObservationReadResults>;
  getInputSet(inputSetId: string): Promise<InputSet>;
  getSchemaRevision(schemaRevisionId: string): Promise<SchemaRevision>;
  listSchemaRevisions(query: { resourceName?: string; limit?: number; after?: string }): Promise<SchemaRevisionPage>;
  listThingTypes(): Promise<{ thingTypes: ThingType[] }>;
  listThings(query: ListThingsQuery): Promise<ThingPage>;
  /** 一次读取最多 256 个事物。 */
  readThings(thingIds: string[]): Promise<ThingReadResults>;
  /** 对象视图：现在的样子、关系和发生过的事。 */
  getThing(thingId: string): Promise<ThingView>;
  listThingReferrers(thingId: string, query: ListReferrersQuery): Promise<ReferrerPage>;
  listObservationReferrers(observationId: string, query: ListReferrersQuery): Promise<ReferrerPage>;
  listObservations(query: ListObservationsQuery): Promise<ObservationPage>;
  /** 读对象视图 activity 给出的 href。 */
  readActivity(
    href: string,
    query: { limit?: number; order?: "asc" | "desc"; after?: string },
  ): Promise<ObservationPage>;
  search(query: SearchQuery): Promise<ObservationPage>;
  deleteObservation(observationId: string): Promise<DeleteResult>;
  deleteThing(thingId: string): Promise<DeleteResult>;
  write(request: WriteRequest): Promise<WriteResponse>;
  /** 按摘要读取二进制内容的字节。 */
  getContent(digest: string): Promise<Blob>;
  /** 请求叙事 Observer 写一段时间，边写边收到结果。 */
  writeNarrative(request: NarrativeRequest, signal: AbortSignal): AsyncIterable<NarrativeEvent>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | undefined;

  constructor(status: number, message: string, problem?: ProblemDetails) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
  }
}

/** 404 和 410 都表示读不到：不存在或已删除。 */
export function isUnreadable(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 404 || error.status === 410);
}

function query(params: object): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null) search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : "";
}

function failure(status: number, text: string): ApiError {
  let problem: ProblemDetails | undefined;
  try {
    problem = JSON.parse(text) as ProblemDetails;
  } catch {
    problem = undefined;
  }
  return new ApiError(status, problem?.detail || problem?.title || `HTTP ${status}`, problem);
}

export function createHttpSource(base = "/api"): OrisumiSource {
  async function send(path: string, init?: RequestInit): Promise<Response> {
    try {
      return await fetch(`${base}${path}`, init);
    } catch (cause) {
      if (init?.signal?.aborted) throw cause;
      throw new ApiError(0, "连不上 Orisumi API", { detail: String(cause) });
    }
  }

  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await send(path, init);
    if (response.status === 204) return undefined as T;
    const text = await response.text();
    if (!response.ok) throw failure(response.status, text);
    return JSON.parse(text) as T;
  }

  const post = (body: unknown): RequestInit => ({
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const id = encodeURIComponent;

  return {
    getObservation: (observationId) => request(`/observations/${id(observationId)}`),
    readObservations: (observationIds) => request("/observations/read", post({ observationIds })),
    getInputSet: (inputSetId) => request(`/input-sets/${id(inputSetId)}`),
    getSchemaRevision: (schemaRevisionId) => request(`/schema-revisions/${id(schemaRevisionId)}`),
    listSchemaRevisions: (params) => request(`/schema-revisions${query(params)}`),
    listThingTypes: () => request("/thing-types"),
    listThings: (params) => request(`/things${query(params)}`),
    readThings: (thingIds) => request("/things/read", post({ thingIds })),
    getThing: (thingId) => request(`/things/${id(thingId)}`),
    listThingReferrers: (thingId, params) => request(`/things/${id(thingId)}/referrers${query(params)}`),
    listObservationReferrers: (observationId, params) =>
      request(`/observations/${id(observationId)}/referrers${query(params)}`),
    listObservations: (params) => request(`/observations${query(params)}`),
    readActivity: (href, params) => {
      const separator = href.includes("?") ? "&" : "?";
      return request(`${href}${query(params).replace("?", separator)}`);
    },
    search: (params) => request(`/search${query(params)}`),
    deleteObservation: (observationId) => request(`/observations/${id(observationId)}`, { method: "DELETE" }),
    deleteThing: (thingId) => request(`/things/${id(thingId)}`, { method: "DELETE" }),
    write: (body) => request("/writes", post(body)),
    getContent: async (digest) => {
      const response = await send(`/contents/${encodeURIComponent(digest)}`);
      if (!response.ok) throw new ApiError(response.status, `HTTP ${response.status}`);
      return response.blob();
    },
    writeNarrative: async function* (body, signal) {
      const response = await send("/narratives", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "text/event-stream" },
        body: JSON.stringify(body),
        signal,
      });
      if (!response.ok) throw failure(response.status, await response.text());
      for await (const { event, data } of parseServerEvents(response.body!)) {
        const value = JSON.parse(data) as Record<string, unknown>;
        if (event === "episode") yield { type: "episode", episode: value as unknown as NarrativeEpisode };
        if (event === "saved") yield { type: "saved", observationIds: value.observationIds as string[] };
        if (event === "failed") yield { type: "failed", detail: value.detail as string };
      }
    },
  };
}

/** 读完一个分页列表。 */
export async function readAllPages(
  load: (after: string | undefined) => Promise<ObservationPage>,
): Promise<Observation[]> {
  const observations: Observation[] = [];
  let after: string | undefined;
  do {
    const page = await load(after);
    observations.push(...page.observations);
    after = page.next ?? undefined;
  } while (after);
  return observations;
}

/** 读完一个事物的分页列表。 */
export async function readAllThings(load: (after: string | undefined) => Promise<ThingPage>): Promise<Thing[]> {
  const things: Thing[] = [];
  let after: string | undefined;
  do {
    const page = await load(after);
    things.push(...page.things);
    after = page.next ?? undefined;
  } while (after);
  return things;
}

/** 读完修订的分页列表。 */
export async function readAllSchemaRevisions(
  load: (after: string | undefined) => Promise<SchemaRevisionPage>,
): Promise<SchemaRevision[]> {
  const revisions: SchemaRevision[] = [];
  let after: string | undefined;
  do {
    const page = await load(after);
    revisions.push(...page.schemaRevisions);
    after = page.next ?? undefined;
  } while (after);
  return revisions;
}
