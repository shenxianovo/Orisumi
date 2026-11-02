import type { Page, Route } from "@playwright/test";

// 用路由拦截在内存里模拟后端：事物、Observation 和类型，按 openapi.json 的形状读写。
// 只实现控制台用到的接口和筛选。

export interface Thing {
  thingId: string;
  schemaId: string;
  keys: { scope: string | null; scheme: string; value: string }[];
  name: string | null;
  nameObservationId: string | null;
}

export interface Observation {
  observationId: string;
  schemaRevisionId: string;
  observerId: string;
  speakerId: string | null;
  observerDescriptionId: string | null;
  formation: string;
  inputSetId: string | null;
  replaces: string[];
  startAt: string | null;
  endAt: string | null;
  timeZone: string | null;
  recordedAt: string;
  content: Record<string, unknown>;
}

/** 一种类型：schemaRevisionId 只有 Observation 类型才用。 */
export interface Type {
  schemaId: string;
  schemaRevisionId: string;
  thing: boolean;
  content: {
    displayName: string;
    description: string;
    resourceName: string;
    fields: Record<string, Record<string, unknown>>;
  };
}

let counter = 0;
export const uuid = () => `0192b056-7300-7000-8000-${(++counter).toString(16).padStart(12, "0")}`;

export const PERSON = "019e1bbb-ec00-7000-8000-000000000006";
export const PROGRAM = "019e1bbb-ec00-7000-8000-000000000012";
export const REVIEW = "019e1bbb-ec00-7000-8000-00000000000e";
export const NAME = "019e1bbb-ec00-7000-8000-000000000010";
export const DESCRIPTION = "019e1bbb-ec00-7000-8000-00000000001c";

const thingRef = (role: string, description = "") => ({
  type: "reference",
  target: "thing",
  role,
  description,
  required: true,
  nullable: false,
});
const observationRef = (description = "") => ({
  type: "reference",
  target: "observation",
  description,
  required: true,
  nullable: true,
});
const text = (description = "") => ({ type: "string", description, required: true, nullable: true });

export interface NarrativeStream {
  status?: number;
  code?: string;
  /** 依次发送的 SSE 事件。 */
  events?: { event: string; data: unknown }[];
  /** 响应之前执行，例如保存这批几件事。 */
  save?: () => void;
}

export class Backend {
  readonly things: Thing[] = [];
  readonly observations: Observation[] = [];
  readonly types: Type[] = [];
  readonly instances: { thingId: string; name: string; packageId: string }[] = [];
  readonly contents = new Map<string, string>();
  /** 收到的 POST /narratives 请求。 */
  readonly narratives: unknown[] = [];
  /** 下一次 POST /narratives 的响应；没有时返回 503。 */
  narrative: NarrativeStream | null = null;
  readonly people: Thing;
  readonly review: Type;
  readonly nameType: Type;
  readonly description: Type;

  constructor() {
    this.schema("people", "人", "thing", {}, PERSON);
    this.schema("program-instances", "程序实例", "thing", {}, PROGRAM);
    this.review = this.schema(
      "reviews",
      "复核",
      "observation",
      { verdict: text(), targetId: observationRef(), scope: text(), text: text() },
      REVIEW,
    );
    this.nameType = this.schema("names", "名字", "observation", { thingId: thingRef("about"), name: text() }, NAME);
    this.description = this.schema(
      "observer-descriptions",
      "Observer 描述",
      "observation",
      { platform: text(), model: text() },
      DESCRIPTION,
    );
    this.people = this.thing(PERSON, { scope: null, scheme: "orisumi.user", value: "owner" });
  }

  schema(
    resourceName: string,
    displayName: string,
    kind: "thing" | "observation",
    fields: Type["content"]["fields"] = {},
    schemaId = uuid(),
  ): Type {
    const type: Type = {
      schemaId,
      schemaRevisionId: uuid(),
      thing: kind === "thing",
      content: { displayName, description: `${displayName}。`, resourceName, fields },
    };
    this.types.push(type);
    return type;
  }

  thing(schemaId: string, key: Thing["keys"][number] | null, name: string | null = null): Thing {
    const thing: Thing = {
      thingId: uuid(),
      schemaId,
      keys: key ? [key] : [],
      name,
      nameObservationId: null,
    };
    this.things.push(thing);
    return thing;
  }

  /** 一个登记过的程序实例和它的描述。 */
  program(
    name: string,
    packageId: string,
    content: Record<string, unknown> = {},
  ): { thing: Thing; description: Observation } {
    const thing = this.thing(PROGRAM, { scope: null, scheme: "test.instance", value: uuid() });
    this.instances.push({ thingId: thing.thingId, name, packageId });
    return { thing, description: this.observe(this.description, thing, content) };
  }

  observe(
    schema: Type,
    observer: Thing,
    content: Record<string, unknown>,
    options: Partial<
      Pick<Observation, "startAt" | "endAt" | "formation" | "inputSetId" | "observerDescriptionId" | "replaces">
    > = {},
  ): Observation {
    const observation: Observation = {
      observationId: uuid(),
      schemaRevisionId: schema.schemaRevisionId,
      observerId: observer.thingId,
      speakerId: null,
      observerDescriptionId: null,
      formation: "direct",
      inputSetId: null,
      replaces: [],
      startAt: null,
      endAt: null,
      timeZone: "Asia/Shanghai",
      recordedAt: new Date().toISOString(),
      content,
      ...options,
    };
    this.observations.push(observation);
    return observation;
  }

  raw(line: unknown) {
    const body = JSON.stringify(line);
    const digest = `sha256:${(++counter).toString(16).padStart(64, "0")}`;
    this.contents.set(digest, body);
    return { digest, mediaType: "application/json", size: body.length };
  }

  private typeOf(observation: Observation): Type | undefined {
    return this.types.find((type) => type.schemaRevisionId === observation.schemaRevisionId);
  }

  private resourceOf(observation: Observation): string | null {
    return this.typeOf(observation)?.content.resourceName ?? null;
  }

  private schemaFor(resourceName: string) {
    return this.types.find((type) => type.content.resourceName === resourceName);
  }

  /** 一份 Observation 引用了什么，位置写成 JSON Pointer。 */
  private references(observation: Observation) {
    const found: { path: string; targetId: string; role: string | null }[] = [
      { path: "/observerId", targetId: observation.observerId, role: null },
    ];
    observation.replaces.forEach((targetId, i) => found.push({ path: `/replaces/${i}`, targetId, role: null }));
    const fields = this.typeOf(observation)?.content.fields ?? {};
    const visit = (value: unknown, path: string, role: string | null) => {
      if (typeof value === "string") {
        if (this.things.some((t) => t.thingId === value) || this.observations.some((v) => v.observationId === value))
          found.push({ path, targetId: value, role });
      } else if (Array.isArray(value)) value.forEach((item, i) => visit(item, `${path}/${i}`, role));
      else if (value && typeof value === "object")
        for (const [key, item] of Object.entries(value)) visit(item, `${path}/${key}`, role);
    };
    for (const [key, value] of Object.entries(observation.content)) {
      const field = fields[key];
      const items = field?.items as { role?: string } | undefined;
      visit(value, `/content/${key}`, (field?.role as string | undefined) ?? items?.role ?? "mention");
    }
    return found;
  }

  /** 写下 Name 后，事物按 SCHEMA-20 带上用户写的名字。 */
  private applyName(observation: Observation) {
    const thing = this.things.find((t) => t.thingId === observation.content.thingId);
    if (!thing) return;
    thing.name = observation.content.name as string;
    thing.nameObservationId = observation.observationId;
  }

  /** 默认视图：被整条否定的和被替换的隐藏，否定 Review 就是撤回。 */
  private current(observation: Observation): boolean {
    const denied = this.observations.some(
      (v) =>
        v.schemaRevisionId === this.review.schemaRevisionId &&
        v.content.targetId === observation.observationId &&
        v.content.verdict === "deny" &&
        !v.startAt &&
        !v.endAt &&
        this.current(v),
    );
    if (denied) return false;
    return !this.observations.some((v) => v.replaces.includes(observation.observationId));
  }

  async serve(page: Page) {
    await page.route(
      (url) => url.pathname.startsWith("/api/"),
      (route) => this.handle(route),
    );
  }

  private page<T>(items: T[], limit: string | null) {
    return items.slice(0, Number(limit ?? 100));
  }

  private matches(observations: Observation[], param: (name: string) => string | null) {
    const from = param("from") ? Date.parse(param("from")!) : null;
    const to = param("to") ? Date.parse(param("to")!) : null;
    let list = observations;
    if (param("resourceName")) list = list.filter((v) => this.resourceOf(v) === param("resourceName"));
    if (param("observerId")) list = list.filter((v) => v.observerId === param("observerId"));
    if (param("formation")) list = list.filter((v) => v.formation === param("formation"));
    if (param("view") !== "all") list = list.filter((v) => this.current(v));
    if (param("thingId"))
      list = list.filter((v) =>
        this.references(v).some(
          (r) => r.targetId === param("thingId") && r.role && (!param("role") || r.role === param("role")),
        ),
      );
    if (from !== null && to !== null)
      list = list.filter((v) => {
        const start = v.startAt ? Date.parse(v.startAt) : null;
        const end = v.endAt ? Date.parse(v.endAt) : null;
        if (start === null && end === null) return false;
        if (start !== null && end !== null && end > start) return start < to && end > from;
        const point = (start ?? end)!;
        return point >= from && point < to;
      });
    return param("order") === "desc" ? [...list].reverse() : list;
  }

  /** 对象视图，按引用的 role 归类。 */
  private view(thing: Thing) {
    const related = this.observations
      .filter((v) => this.current(v))
      .map((observation) => ({
        observation,
        references: this.references(observation).filter((r) => r.role),
      }));
    const about = (references: { targetId: string; role: string | null }[]) =>
      references.filter((r) => r.role === "about").map((r) => r.targetId);
    const mine = related.filter(({ references }) => references.some((r) => r.targetId === thing.thingId));
    const counts = new Map<string, { resourceName: string; role: string; count: number }>();
    for (const { observation, references } of mine)
      for (const role of new Set(references.filter((r) => r.targetId === thing.thingId).map((r) => r.role!))) {
        if (role === "about") continue;
        const resourceName = this.resourceOf(observation)!;
        const key = `${resourceName}|${role}`;
        const entry = counts.get(key) ?? { resourceName, role, count: 0 };
        entry.count += 1;
        counts.set(key, entry);
      }
    return {
      thing,
      sameAsThingIds: [],
      state: mine
        .filter(({ references }) => about(references).includes(thing.thingId))
        .filter(({ references }) => about(references).every((id) => id === thing.thingId))
        .map(({ observation }) => observation),
      relations: mine
        .filter(({ references }) => about(references).includes(thing.thingId))
        .filter(({ references }) => about(references).some((id) => id !== thing.thingId))
        .map(({ observation }) => observation),
      activity: [...counts.values()].map((item) => ({
        ...item,
        href: `/observations?thingId=${thing.thingId}&role=${item.role}&resourceName=${item.resourceName}`,
      })),
    };
  }

  private handle(route: Route) {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.replace(/^\/api/, "");
    const param = (name: string) => url.searchParams.get(name);
    const json = (body: unknown) => route.fulfill({ json: body });
    const notFound = () => route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } });
    const unknownResource = () => {
      const resourceName = param("resourceName");
      return resourceName !== null && !this.schemaFor(resourceName);
    };

    if (path === "/observations") {
      if (unknownResource()) return notFound();
      const observations = this.page(this.matches(this.observations, param), param("limit"));
      return json({ observations, next: null });
    }
    if (path === "/search") {
      const q = param("q")!;
      const observations = this.matches(this.observations, param).filter((v) => JSON.stringify(v.content).includes(q));
      return json({ observations: this.page(observations, param("limit")), next: null });
    }
    if (path === "/schema-revisions")
      return json({
        schemaRevisions: this.types
          .filter((type) => !type.thing)
          .map(({ schemaRevisionId, schemaId, content }) => ({
            schemaRevisionId,
            schemaId,
            recordedAt: new Date(0).toISOString(),
            content,
          })),
        next: null,
      });
    if (path === "/thing-types")
      return json({
        thingTypes: this.types
          .filter((type) => type.thing)
          .map(({ schemaId, content: { displayName, description, resourceName } }) => ({
            schemaId,
            displayName,
            description,
            resourceName,
          })),
      });
    if (path === "/observer-instances") return json({ instances: this.instances });
    if (path === "/hosts") return json({ hosts: [] });
    if (path === "/things") {
      if (unknownResource()) return notFound();
      const schema = param("resourceName") ? this.schemaFor(param("resourceName")!) : null;
      return json({ things: this.things.filter((t) => !schema || t.schemaId === schema.schemaId), next: null });
    }
    if (path === "/things/read") {
      const { thingIds } = request.postDataJSON() as { thingIds: string[] };
      return json({
        results: thingIds.map((thingId) => {
          const thing = this.things.find((t) => t.thingId === thingId);
          return thing ? { thingId, status: "found", thing } : { thingId, status: "not_found" };
        }),
      });
    }
    if (path === "/observations/read") {
      const { observationIds } = request.postDataJSON() as { observationIds: string[] };
      return json({
        results: observationIds.map((observationId) => {
          const observation = this.observations.find((v) => v.observationId === observationId);
          return observation ? { observationId, status: "found", observation } : { observationId, status: "not_found" };
        }),
      });
    }
    if (path === "/writes") {
      const body = request.postDataJSON() as {
        writes: { schemaId: string; formation: string; content: Record<string, unknown> }[];
      };
      const results = body.writes.map((write) => {
        const schema = this.types.find((type) => type.schemaId === write.schemaId)!;
        return {
          observation: this.observe(schema, this.people, write.content, { formation: write.formation }),
          changed: true,
          replaced: [],
        };
      });
      for (const { observation } of results)
        if (observation.schemaRevisionId === this.nameType.schemaRevisionId) this.applyName(observation);
      return json({ results });
    }
    if (path === "/narratives") {
      this.narratives.push(request.postDataJSON());
      const reply = this.narrative ?? { status: 503, code: "narrative_unavailable" };
      this.narrative = null;
      reply.save?.();
      if (reply.status)
        return route.fulfill({
          status: reply.status,
          json: { title: "Problem", status: reply.status, code: reply.code },
        });
      const body = (reply.events ?? []).map((e) => `event: ${e.event}\ndata: ${JSON.stringify(e.data)}\n\n`).join("");
      return route.fulfill({ status: 200, contentType: "text/event-stream", body });
    }

    const [, kind, id, rest] = path.split("/");
    if (kind === "contents") {
      const body = this.contents.get(decodeURIComponent(id!));
      return body ? route.fulfill({ body, contentType: "application/octet-stream" }) : notFound();
    }
    if (kind === "schema-revisions") {
      const type = this.types.find((item) => item.schemaRevisionId === id);
      if (!type) return notFound();
      const { schemaRevisionId, schemaId, content } = type;
      return json({ schemaRevisionId, schemaId, recordedAt: new Date(0).toISOString(), content });
    }
    if ((kind === "observations" || kind === "things") && rest === "referrers") {
      let referrers = this.observations
        .map((observation) => ({
          observation,
          references: this.references(observation).filter((r) => r.targetId === id),
        }))
        .filter((r) => r.references.length);
      if (param("view") !== "all") referrers = referrers.filter((r) => this.current(r.observation));
      if (param("path")) referrers = referrers.filter((r) => r.references.some((l) => l.path === param("path")));
      if (param("resourceName"))
        referrers = referrers.filter((r) => this.resourceOf(r.observation) === param("resourceName"));
      if (param("order") === "desc") referrers.reverse();
      return json({ referrers: this.page(referrers, param("limit")), next: null });
    }
    if (kind === "observations") {
      const found = this.observations.find((v) => v.observationId === id);
      if (!found) return notFound();
      if (request.method() === "DELETE") {
        this.observations.splice(this.observations.indexOf(found), 1);
        return json({ deleted: [id], dependents: [] });
      }
      return json(found);
    }
    if (kind === "things" && id) {
      const thing = this.things.find((t) => t.thingId === id);
      return thing ? json(this.view(thing)) : notFound();
    }
    return notFound();
  }
}
