import type { Page, Route } from "@playwright/test";

// 用路由拦截在内存里模拟后端：事物、Observation、EntitySchema 和 InputSet，按 openapi.json 的形状读写。
// 只实现控制台用到的接口和筛选。

export interface Thing {
  entityId: string;
  category: "thing";
  schemaId: string;
  keys: { scope: string | null; scheme: string; value: string }[];
  name: string | null;
  nameVersionId: string | null;
}

export interface Version {
  entityId: string;
  versionId: string;
  previousVersionId: string | null;
  category: "observation" | "entity_schema" | "input_set";
  recordedAt: string;
  content: Record<string, unknown>;
  schemaVersionId?: string;
  observerId?: string;
  observerVersionId?: string | null;
  formation?: string;
  inputSetVersionId?: string | null;
  startAt?: string | null;
  endAt?: string | null;
  timeZone?: string | null;
}

let counter = 0;
export const uuid = () => `0192b056-7300-7000-8000-${(++counter).toString(16).padStart(12, "0")}`;

export const PERSON = "019e1bbb-ec00-7000-8000-000000000006";
export const PROGRAM = "019e1bbb-ec00-7000-8000-000000000012";
export const REVIEW = "019e1bbb-ec00-7000-8000-00000000000e";
export const NAME = "019e1bbb-ec00-7000-8000-000000000010";

const thingRef = (description = "") => ({
  type: "reference",
  target: "thing",
  description,
  required: true,
  nullable: false,
});
const versionRef = (description = "") => ({
  type: "reference",
  target: "version",
  description,
  required: true,
  nullable: false,
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
  readonly versions: Version[] = [];
  readonly contents = new Map<string, string>();
  /** 收到的 POST /narratives 请求。 */
  readonly narratives: unknown[] = [];
  /** 下一次 POST /narratives 的响应；没有时返回 503。 */
  narrative: NarrativeStream | null = null;
  readonly people: Thing;
  readonly review: Version;
  readonly nameType: Version;

  constructor() {
    this.schema("people", "人", "thing", {}, PERSON);
    this.schema("program-instances", "程序实例", "thing", {}, PROGRAM);
    this.review = this.schema(
      "reviews",
      "复核",
      "observation",
      { targetVersionId: versionRef(), verdict: text(), text: text() },
      REVIEW,
    );
    this.nameType = this.schema("names", "名字", "observation", { thingId: thingRef(), name: text() }, NAME);
    this.people = this.thing(PERSON, { scope: null, scheme: "orisumi.user", value: "owner" });
  }

  schema(
    resourceName: string,
    displayName: string,
    kind: "thing" | "observation",
    fields: Record<string, unknown> = {},
    entityId = uuid(),
  ): Version {
    const version: Version = {
      entityId,
      versionId: uuid(),
      previousVersionId: null,
      category: "entity_schema",
      recordedAt: new Date().toISOString(),
      content: { displayName, description: `${displayName}。`, resourceName, kind, immutable: false, fields },
    };
    this.versions.push(version);
    return version;
  }

  thing(schemaId: string, key: Thing["keys"][number] | null, name: string | null = null): Thing {
    const thing: Thing = {
      entityId: uuid(),
      category: "thing",
      schemaId,
      keys: key ? [key] : [],
      name,
      nameVersionId: null,
    };
    this.things.push(thing);
    return thing;
  }

  /** 一个程序实例和它的描述。 */
  program(description: Version, content: Record<string, unknown> = {}): { thing: Thing; description: Version } {
    const thing = this.thing(PROGRAM, { scope: null, scheme: "test.instance", value: uuid() });
    return { thing, description: this.observe(description, thing, content, { observerVersionId: null }) };
  }

  observe(
    schema: Version,
    observer: Thing,
    content: Record<string, unknown>,
    options: Partial<Pick<Version, "startAt" | "endAt" | "formation" | "inputSetVersionId" | "observerVersionId">> = {},
  ): Version {
    const version: Version = {
      entityId: uuid(),
      versionId: uuid(),
      previousVersionId: null,
      category: "observation",
      schemaVersionId: schema.versionId,
      observerId: observer.entityId,
      observerVersionId: null,
      formation: "direct",
      inputSetVersionId: null,
      startAt: null,
      endAt: null,
      timeZone: "Asia/Shanghai",
      recordedAt: new Date().toISOString(),
      content,
      ...options,
    };
    this.versions.push(version);
    return version;
  }

  raw(line: unknown) {
    const body = JSON.stringify(line);
    const digest = `sha256:${(++counter).toString(16).padStart(64, "0")}`;
    this.contents.set(digest, body);
    return { digest, mediaType: "application/json", size: body.length };
  }

  private resourceOf(version: Version): string | null {
    const schema = this.versions.find((v) => v.versionId === version.schemaVersionId);
    return (schema?.content.resourceName as string | undefined) ?? null;
  }

  private schemaFor(resourceName: string) {
    return this.versions.find((v) => v.category === "entity_schema" && v.content.resourceName === resourceName);
  }

  /** 一个版本引用了哪些实体，位置写成 JSON Pointer。 */
  private references(version: Version) {
    const found: { path: string; targetEntityId: string; targetVersionId: string | null }[] = [];
    if (version.observerId)
      found.push({ path: "/observerId", targetEntityId: version.observerId, targetVersionId: null });
    const visit = (value: unknown, path: string) => {
      if (typeof value === "string") {
        const thing = this.things.find((t) => t.entityId === value);
        const target = this.versions.find((v) => v.versionId === value);
        if (thing) found.push({ path, targetEntityId: thing.entityId, targetVersionId: null });
        else if (target) found.push({ path, targetEntityId: target.entityId, targetVersionId: target.versionId });
      } else if (Array.isArray(value)) value.forEach((item, i) => visit(item, `${path}/${i}`));
      else if (value && typeof value === "object")
        for (const [key, item] of Object.entries(value)) visit(item, `${path}/${key}`);
    };
    visit(version.content, "/content");
    return found;
  }

  /** 写下 Name 后，事物按 UNDERSTANDING-9 带上用户写的名字。 */
  private applyName(version: Version) {
    const thing = this.things.find((t) => t.entityId === version.content.thingId);
    if (!thing) return;
    thing.name = version.content.name as string;
    thing.nameVersionId = version.versionId;
  }

  /** 当前理解：没有针对整条的否定，被取代时有用户的确认。 */
  private current(version: Version): boolean {
    const reviews = this.versions.filter(
      (v) => v.schemaVersionId === this.review.versionId && v.content.targetVersionId === version.versionId,
    );
    const by = (verdict: string) => reviews.filter((r) => r.content.verdict === verdict);
    if (by("deny").some((r) => !r.startAt && !r.endAt)) return false;
    return !by("supersede").length || by("confirm").some((r) => r.observerId === this.people.entityId);
  }

  async serve(page: Page) {
    await page.route(
      (url) => url.pathname.startsWith("/api/"),
      (route) => this.handle(route),
    );
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
      const from = param("from") ? Date.parse(param("from")!) : null;
      const to = param("to") ? Date.parse(param("to")!) : null;
      let versions = this.versions.filter((v) => v.category === "observation");
      if (param("resourceName")) versions = versions.filter((v) => this.resourceOf(v) === param("resourceName"));
      if (param("observerId")) versions = versions.filter((v) => v.observerId === param("observerId"));
      if (param("formation")) versions = versions.filter((v) => v.formation === param("formation"));
      if (param("standing") === "current") versions = versions.filter((v) => this.current(v));
      if (from !== null && to !== null)
        versions = versions.filter((v) => {
          const start = v.startAt ? Date.parse(v.startAt) : null;
          const end = v.endAt ? Date.parse(v.endAt) : null;
          if (start === null && end === null) return false;
          if (start !== null && end !== null && end > start) return start < to && end > from;
          const point = (start ?? end)!;
          return point >= from && point < to;
        });
      return json({ versions, next: null });
    }
    if (path === "/entities") {
      if (unknownResource()) return notFound();
      const category = param("category");
      const versions = this.versions.filter(
        (v) =>
          (!category || v.category === category) &&
          (!param("resourceName") || this.resourceOf(v) === param("resourceName")),
      );
      return json({ versions, next: null });
    }
    if (path === "/things") {
      if (unknownResource()) return notFound();
      const schema = param("resourceName") ? this.schemaFor(param("resourceName")!) : null;
      return json({ things: this.things.filter((t) => !schema || t.schemaId === schema.entityId), next: null });
    }
    if (path === "/things/read") {
      const { entityIds } = request.postDataJSON() as { entityIds: string[] };
      return json({
        results: entityIds.map((entityId) => {
          const thing = this.things.find((t) => t.entityId === entityId);
          return thing ? { entityId, status: "found", thing } : { entityId, status: "not_found" };
        }),
      });
    }
    if (path === "/versions/read") {
      const { versionIds } = request.postDataJSON() as { versionIds: string[] };
      return json({
        results: versionIds.map((versionId) => {
          const version = this.versions.find((v) => v.versionId === versionId);
          return version ? { versionId, status: "found", version } : { versionId, status: "not_found" };
        }),
      });
    }
    if (path === "/writes") {
      const body = request.postDataJSON() as {
        writes: { schemaId: string; formation: string; content: Record<string, unknown> }[];
      };
      const results = body.writes.map((write) => {
        const schema = this.versions.find((v) => v.entityId === write.schemaId && v.category === "entity_schema")!;
        return {
          version: this.observe(schema, this.people, write.content, { formation: write.formation }),
          changed: true,
        };
      });
      for (const { version } of results)
        if (version.schemaVersionId === this.nameType.versionId) this.applyName(version);
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
    if (kind === "versions") {
      const found = this.versions.find((v) => v.versionId === id);
      return found ? json(found) : notFound();
    }
    if (kind === "entities" && id) {
      const thing = this.things.find((t) => t.entityId === id);
      const chain = this.versions.filter((v) => v.entityId === id);
      if (!thing && !chain.length) return notFound();
      if (request.method() === "DELETE") {
        this.versions.splice(0, this.versions.length, ...this.versions.filter((v) => v.entityId !== id));
        return route.fulfill({ status: 204 });
      }
      if (rest === "versions") return json({ versions: chain });
      if (rest === "referrers") {
        let referrers = this.versions
          .map((version) => ({
            version,
            references: this.references(version).filter((r) => r.targetEntityId === id),
          }))
          .filter((r) => r.references.length);
        if (param("path")) referrers = referrers.filter((r) => r.references.some((l) => l.path === param("path")));
        if (param("resourceName"))
          referrers = referrers.filter((r) => this.resourceOf(r.version) === param("resourceName"));
        if (param("order") === "desc") referrers.reverse();
        const limit = Number(param("limit") ?? 100);
        return json({ referrers: referrers.slice(0, limit), next: null });
      }
      return json(thing ?? chain.at(-1));
    }
    return notFound();
  }
}
