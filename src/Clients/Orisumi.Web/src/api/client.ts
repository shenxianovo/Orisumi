import { parseServerEvents } from "@/lib/sse";
import type {
  ListEntitiesQuery,
  ListObservationsQuery,
  ListReferrersQuery,
  ListThingsQuery,
  NarrativeEpisode,
  NarrativeEvent,
  NarrativeRequest,
  ProblemDetails,
  ReferrerPage,
  Thing,
  ThingPage,
  ThingReadResults,
  Version,
  VersionList,
  VersionPage,
  VersionReadResults,
  WriteRequest,
  WriteResponse,
} from "./types";

/** 控制台读写数据的接口，HTTP 实现读写后端。 */
export interface OrisumiSource {
  getVersion(versionId: string): Promise<Version>;
  /** 一次读取最多 256 个版本。 */
  readVersions(versionIds: string[]): Promise<VersionReadResults>;
  /** 事物返回它自己，有版本的实体返回当前版本。 */
  getEntity(entityId: string): Promise<Thing | Version>;
  listEntities(query: ListEntitiesQuery): Promise<VersionPage>;
  listThings(query: ListThingsQuery): Promise<ThingPage>;
  /** 一次读取最多 256 个事物。 */
  readThings(entityIds: string[]): Promise<ThingReadResults>;
  listEntityVersions(entityId: string): Promise<VersionList>;
  listReferrers(entityId: string, query: ListReferrersQuery): Promise<ReferrerPage>;
  listObservations(query: ListObservationsQuery): Promise<VersionPage>;
  deleteEntity(entityId: string): Promise<void>;
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

  return {
    getVersion: (versionId) => request(`/versions/${encodeURIComponent(versionId)}`),
    readVersions: (versionIds) =>
      request("/versions/read", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ versionIds }),
      }),
    getEntity: (entityId) => request(`/entities/${encodeURIComponent(entityId)}`),
    listEntities: (params) => request(`/entities${query(params)}`),
    listThings: (params) => request(`/things${query(params)}`),
    readThings: (entityIds) =>
      request("/things/read", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ entityIds }),
      }),
    listEntityVersions: (entityId) => request(`/entities/${encodeURIComponent(entityId)}/versions`),
    listReferrers: (entityId, params) => request(`/entities/${encodeURIComponent(entityId)}/referrers${query(params)}`),
    listObservations: (params) => request(`/observations${query(params)}`),
    deleteEntity: (entityId) => request(`/entities/${encodeURIComponent(entityId)}`, { method: "DELETE" }),
    write: (body) =>
      request("/writes", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      }),
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
        if (event === "saved") yield { type: "saved", versionIds: value.versionIds as string[] };
        if (event === "failed") yield { type: "failed", detail: value.detail as string };
      }
    },
  };
}

/** 读完一个分页列表。 */
export async function readAllPages(load: (after: string | undefined) => Promise<VersionPage>): Promise<Version[]> {
  const versions: Version[] = [];
  let after: string | undefined;
  do {
    const page = await load(after);
    versions.push(...page.versions);
    after = page.next ?? undefined;
  } while (after);
  return versions;
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
