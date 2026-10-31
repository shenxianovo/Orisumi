import { expect, test, type APIRequestContext } from "@playwright/test";
import { uuidv7 } from "../../src/lib/uuid";

// 经本地环境的 Web 入口验证：页面、同源 API、Vite HMR WebSocket，以及真实数据在档案、面板、类型图、Observer 页面和实体页中的展示。
// 运行前先 `orisumi env up web`，入口地址取自 `orisumi env status --json`，默认是主检出目录的 8081。
const BASE = process.env.WEB_URL ?? "http://localhost:8081";

const PROGRAM = "019e1bbb-ec00-7000-8000-000000000012";

const string = (description: string) => ({ type: "string", description, required: true, nullable: false });
const thingRef = (description: string) => ({
  type: "reference",
  target: "thing",
  description,
  required: true,
  nullable: false,
});

interface Declared {
  schemaId: string;
  content: Record<string, unknown>;
}

function declare(displayName: string, resourceName: string, kind: "thing" | "observation", fields = {}): Declared {
  return {
    schemaId: uuidv7(),
    content: { displayName, description: `Web e2e 写入的${displayName}`, resourceName, kind, immutable: false, fields },
  };
}

/**
 * 以一个直接运行的程序实例写入：声明类型、写下描述，再写入 Observation。返回保存的 Observation，
 * 以及清理时要删除的实体：Observer 写下的全部 Observation、提到的事物、Observer 自己，最后是类型。
 */
async function write(
  request: APIRequestContext,
  description: Declared,
  schemas: Declared[],
  writes: Record<string, unknown>[],
) {
  const response = await request.post(`${BASE}/api/writes`, {
    data: {
      observer: { schemaId: PROGRAM, key: { scope: null, scheme: "e2e.instance", value: uuidv7() } },
      schemas: [description, ...schemas],
      description: { schemaId: description.schemaId, content: { label: "e2e" } },
      writes,
    },
  });
  expect(response.status(), await response.text()).toBe(200);
  const results = (await response.json()) as { results: { version?: Record<string, unknown>; error?: unknown }[] };
  expect(
    results.results.every((result) => result.version),
    JSON.stringify(results),
  ).toBe(true);
  const saved = results.results.map((result) => result.version!);
  const observerId = saved[0]!.observerId as string;
  return {
    saved,
    observerId,
    async cleanup(things: string[] = []) {
      const written = await request.get(`${BASE}/api/entities/${observerId}/referrers?path=/observerId&limit=1000`);
      const referrers = ((await written.json()) as { referrers: { version: { entityId: string } }[] }).referrers;
      for (const entityId of [
        ...new Set(referrers.map((r) => r.version.entityId)),
        ...things,
        observerId,
        ...[description, ...schemas].map((schema) => schema.schemaId),
      ])
        await request.delete(`${BASE}/api/entities/${entityId}`);
    },
  };
}

async function seed(request: APIRequestContext) {
  const stamp = Date.now();
  const name = `E2E Observer ${stamp}`;
  const now = new Date().toISOString();
  const observer = declare(name, `e2e-observers-${stamp}`, "observation", { label: string("Smoke test label") });
  const content = declare(`E2E 内容 ${stamp}`, `e2e-contents-${stamp}`, "observation", {
    label: string("Smoke test label"),
  });
  const written = await write(
    request,
    observer,
    [content],
    [
      {
        schemaId: content.schemaId,
        formation: "direct",
        content: { label: "e2e" },
        time: { startAt: now, endAt: now, timeZone: "Asia/Shanghai" },
      },
    ],
  );
  return { name, observerId: written.observerId, cleanup: () => written.cleanup() };
}

test("web edge: page loads, same-origin API works, HMR websocket opens", async ({ page }) => {
  const wsUrls: string[] = [];
  const consoleErrors: string[] = [];
  page.on("websocket", (ws) => wsUrls.push(ws.url()));
  page.on("console", (msg) => {
    if (msg.type() === "error") consoleErrors.push(msg.text());
  });

  await page.goto(`${BASE}/observers`, { waitUntil: "domcontentloaded" });
  await expect(page.getByLabel("现在的状态")).toBeVisible();

  await expect.poll(() => wsUrls.length, { timeout: 10_000 }).toBeGreaterThan(0);
  expect(wsUrls.some((u) => u.startsWith(BASE.replace(/^http/, "ws")))).toBe(true);

  const res = await page.request.get(`${BASE}/api/entities/0192b056-7300-7000-8000-0000000000ff`);
  expect(res.status()).toBe(404);

  await page.goto(`${BASE}/entities`, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("link", { name: "原始记录", exact: true })).toBeVisible();

  const viteWsErrors = consoleErrors.filter((t) => /WebSocket|vite/i.test(t));
  expect(viteWsErrors).toEqual([]);
});

test("web edge: a submitted Observation gets a row on the Observer page and opens its Observer", async ({
  page,
  request,
}) => {
  const data = await seed(request);
  try {
    await page.goto(`${BASE}/observers`);
    const row = page.getByRole("listitem").filter({ hasText: data.name });
    await expect(row).toContainText("今天 1 份");
    await row.getByRole("button").click();
    const panel = page.getByRole("region", { name: new RegExp(data.name) });
    await expect(panel.getByRole("heading", { name: data.name })).toBeVisible();
    await panel.getByText("ID 和原始数据").click();
    await panel.getByRole("link", { name: "实体页 →" }).click();
    await expect(page.getByRole("heading", { name: data.name })).toBeVisible();
    // 引用 Observer 的是它的描述和它写下的那一份。
    await expect(page.getByRole("list", { name: "被引用" }).getByRole("link")).toHaveCount(2);
  } finally {
    await data.cleanup();
  }
});

/** 一份 8 分钟的时段，提到一个事物，和前台状态 Observer 的结构相同。 */
async function seedThing(request: APIRequestContext) {
  const stamp = Date.now();
  const names = {
    thing: `e2e.thing.${stamp}`,
    thingType: `E2E 事物 ${stamp}`,
    thingResource: `e2e-things-${stamp}`,
    seenAs: `E2E 应用 ${stamp}`,
    observer: `E2E 观测者 ${stamp}`,
  };
  const thing = declare(names.thingType, names.thingResource, "thing");
  const period = declare(`E2E 时段 ${stamp}`, `e2e-periods-${stamp}`, "observation", {
    name: string("当时读到的名字"),
    thingId: thingRef("这一段关于的事物"),
  });
  const observer = declare(names.observer, `e2e-thing-observers-${stamp}`, "observation", { label: string("标签") });
  const end = Date.now() - 2 * 60_000;
  const written = await write(
    request,
    observer,
    [thing, period],
    [
      {
        schemaId: period.schemaId,
        formation: "direct",
        content: {
          name: names.seenAs,
          thingId: { schemaId: thing.schemaId, key: { scope: null, scheme: "e2e.thing", value: names.thing } },
        },
        time: {
          startAt: new Date(end - 8 * 60_000).toISOString(),
          endAt: new Date(end).toISOString(),
          timeZone: "Asia/Shanghai",
        },
      },
    ],
  );
  const thingId = (written.saved[0]!.content as { thingId: string }).thingId;
  return { names, thingId, cleanup: () => written.cleanup([thingId]) };
}

test("web edge: a thing has an archive, opens stacked panels, shows on the type map and grows a weave", async ({
  page,
  request,
}) => {
  const data = await seedThing(request);
  try {
    await page.goto(`${BASE}/things/${data.thingId}`);
    await expect(page.getByRole("heading", { name: data.names.thing })).toBeVisible();

    // 点档案里的陈述推出它的面板。
    await page.getByRole("button", { name: new RegExp(data.names.seenAs) }).click();
    const panels = page.getByRole("complementary", { name: "面板" });
    await expect(panels.getByRole("heading", { name: data.names.seenAs })).toBeVisible();
    await expect(page).toHaveURL(/stack=/);

    // 面板里的记号接着打开下一层，Esc 关掉最后一层。
    await panels
      .getByRole("button", { name: new RegExp(data.names.observer) })
      .first()
      .click();
    const observer = panels.getByRole("region", { name: new RegExp(data.names.observer) });
    await expect(observer).toBeVisible();
    await expect(observer).toContainText("份 Observation");
    await page.keyboard.press("Escape");
    await expect(observer).toHaveCount(0);

    // 类型图里事物在“事物”一列，选中后列出它的实体。
    await page.goto(`${BASE}/entities?type=${data.names.thingResource}`);
    await expect(page.getByRole("button", { name: new RegExp(data.names.thingType), pressed: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: data.names.thingType })).toBeVisible();
    await expect(page.getByRole("button", { name: new RegExp(data.names.thing) }).first()).toBeVisible();

    // 点类型下的实体，从它开始织：事物左边长出提到它的时段，时段左边长出 Observer；停在 Observer 上时，路上的标签亮起。
    await page
      .getByRole("button", { name: new RegExp(data.names.thing) })
      .first()
      .click();
    await expect(page).toHaveURL(/root=/);
    await page.getByRole("button", { name: new RegExp(`${data.names.thingType} ${data.names.thing}`) }).click();
    const period = page.getByRole("button", { name: new RegExp(data.names.seenAs) });
    await expect(period).toBeVisible();
    await expect(period).toContainText(data.names.observer);
    await period.click();
    const grown = page.getByRole("button", { name: new RegExp(data.names.observer) }).last();
    await expect(grown).toBeVisible();
    await grown.hover();
    await expect(page.locator("svg text", { hasText: /^来源$/ })).toBeVisible();
    await expect(page.locator("svg text", { hasText: /^thing$/ })).toBeVisible();
  } finally {
    await data.cleanup();
  }
});
