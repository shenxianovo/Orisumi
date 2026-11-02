import { expect, test, type Page } from "@playwright/test";
import { Backend, type Thing } from "./backend";

// 今天页面：几件事的写作和读取、一天的条，以及只算前台应用时段的应用排行。

const day = new Date();
const at = (hours: number, minutes: number) =>
  new Date(day.getFullYear(), day.getMonth(), day.getDate(), hours, minutes).toISOString();

const ref = { type: "reference", target: "thing", description: "", required: true, nullable: false };
const refs = { type: "array", items: ref, description: "", required: true, nullable: false };
const text = { type: "string", description: "", required: true, nullable: true };

function seed() {
  const backend = new Backend();
  const installs = backend.schema("local-applications", "本机应用", "thing");
  const devices = backend.schema("devices", "设备", "thing");
  const sessions = backend.schema("assistant-sessions", "助手会话", "thing");
  const periods = backend.schema("foreground-application-periods", "前台应用时段", "observation", {
    name: text,
    localApplicationId: { ...ref, role: "part" },
  });
  const interactions = backend.schema("assistant-interactions", "助手交互", "observation", {
    sessionId: { ...ref, role: "part" },
    deviceId: ref,
  });
  const episodes = backend.schema("episodes", "一件事", "observation", {
    title: text,
    summary: text,
    sessionIds: refs,
    localApplicationIds: refs,
  });
  const capture = backend.program("前台状态 Observer", "foreground-state", { platform: "macos" });
  const agent = backend.program("助手会话 Observer", "assistant-sessions", { platform: "macos" });
  const writer = backend.program("叙事 Observer", "narrative", { model: "openai/gpt-6.1-terra" });

  const code = backend.thing(
    installs.schemaId,
    { scope: "mac", scheme: "macos.installation", value: "com.microsoft.VSCode\n/Applications/Code.app" },
    "Visual Studio Code",
  );
  const mac = backend.thing(devices.schemaId, { scope: null, scheme: "macos.hardware-uuid", value: "J929" });
  const session = (id: string, title: string): Thing =>
    backend.thing(sessions.schemaId, { scope: null, scheme: "claude-code.session", value: id }, title);
  const talked = session("a", "讨论建模");
  const silent = session("b", "子 Agent 的会话");

  const interact = (target: Thing, hours: number, minutes: number, actor: string) =>
    backend.observe(
      interactions,
      agent.thing,
      {
        sessionId: target.thingId,
        deviceId: mac.thingId,
        kind: "message",
        actor,
        sourceKind: "text",
        sourceId: `${target.thingId}-${hours}-${minutes}`,
        parts: [],
        sourceEntries: [],
      },
      {
        startAt: at(hours, minutes),
        endAt: at(hours, minutes + 1),
        observerDescriptionId: agent.description.observationId,
      },
    );
  interact(talked, 9, 0, "user");
  interact(talked, 9, 1, "assistant");
  interact(silent, 9, 2, "assistant");
  backend.observe(
    periods,
    capture.thing,
    { name: "Code", localApplicationId: code.thingId },
    { startAt: at(9, 0), endAt: at(9, 30), observerDescriptionId: capture.description.observationId },
  );

  const episodeContent = {
    title: "讨论事物模型",
    summary: "你和助手确定了事物只有身份。",
    sessionIds: [talked.thingId, silent.thingId],
    localApplicationIds: [code.thingId],
  };
  const streamed = { startAt: at(9, 0), endAt: at(9, 30), ...episodeContent };
  const saveEpisode = () =>
    backend.observe(episodes, writer.thing, episodeContent, {
      formation: "inferred",
      startAt: at(9, 0),
      endAt: at(9, 30),
      observerDescriptionId: writer.description.observationId,
    });
  return { backend, streamed, saveEpisode, code, talked, mac };
}

async function open(backend: Backend, page: Page) {
  await backend.serve(page);
  await page.goto("/");
}

test("排行只列本机应用，设备和会话不进排行，总时长只算前台时段", async ({ page }) => {
  const { backend, saveEpisode } = seed();
  saveEpisode();
  await open(backend, page);
  const applications = page.getByRole("region", { name: "应用" });
  await expect(applications.getByText("30 分钟 · 在 1 个应用之间切换了 0 次")).toBeVisible();
  await expect(applications.getByRole("listitem")).toHaveCount(1);
  await expect(applications.getByRole("button", { name: /Visual Studio Code/ })).toBeVisible();
  await expect(applications.getByText("J929")).toHaveCount(0);
});

test("已经写下的几件事：不再请求，写出模型、用到的应用和说过话的会话", async ({ page }) => {
  const { backend, saveEpisode } = seed();
  saveEpisode();
  await open(backend, page);
  const episodes = page.getByRole("region", { name: "几件事" });
  await expect(episodes.getByRole("heading", { name: "讨论事物模型" })).toBeVisible();
  await expect(episodes.getByText("openai/gpt-6.1-terra")).toBeVisible();
  await expect(episodes.getByRole("button", { name: /Visual Studio Code/ })).toContainText("30 分钟");
  await expect(episodes.getByRole("button", { name: /讨论建模/ })).toBeVisible();
  await expect(episodes.getByText("另有 1 个没有发言的会话")).toBeVisible();
  await expect(page.getByText("1 件事 · 2 个助手会话，你说了 1 句 · 前台记录 30 分钟")).toBeVisible();
  expect(backend.narratives).toEqual([]);
});

test("这一天还没有写时请求写，收到 saved 后读取保存下来的", async ({ page }) => {
  const { backend, streamed, saveEpisode } = seed();
  const saved = saveEpisode();
  backend.observations.splice(backend.observations.indexOf(saved), 1);
  backend.narrative = {
    events: [
      { event: "episode", data: streamed },
      { event: "saved", data: { observationIds: [saved.observationId] } },
    ],
    save: () => backend.observations.push(saved),
  };
  await open(backend, page);
  await expect(page.getByRole("heading", { name: "讨论事物模型" })).toBeVisible();
  await expect(page.getByText("叙事 Observer · 推断")).toBeVisible();
  expect(backend.narratives).toHaveLength(1);
  expect((backend.narratives[0] as { timeZone: string }).timeZone).toBeTruthy();
});

test("写作失败时撤下已经显示的几件事，写出原因和重写", async ({ page }) => {
  const { backend, streamed } = seed();
  backend.narrative = {
    events: [
      { event: "episode", data: streamed },
      { event: "failed", data: { detail: "模型超时" } },
    ],
  };
  await open(backend, page);
  await expect(page.getByRole("alert")).toContainText("模型超时");
  await expect(page.getByRole("heading", { name: "讨论事物模型" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "重写" })).toBeVisible();
  expect(backend.narratives).toHaveLength(1);
});

test("叙事 Observer 没有配置 key 时，页面其余部分照常", async ({ page }) => {
  const { backend } = seed();
  await open(backend, page);
  await expect(page.getByText("叙事 Observer 没有配置 API Key")).toBeVisible();
  await expect(page.getByRole("region", { name: "应用" }).getByRole("listitem")).toHaveCount(1);
});

test("另一次写作在写时等它写完，读取结果", async ({ page }) => {
  test.slow();
  const { backend, saveEpisode } = seed();
  backend.narrative = { status: 409, code: "narrative_in_progress" };
  await open(backend, page);
  await expect(page.getByText("正在写")).toBeVisible();
  saveEpisode();
  await expect(page.getByRole("heading", { name: "讨论事物模型" })).toBeVisible({ timeout: 10_000 });
  expect(backend.narratives).toHaveLength(1);
});

test("记录之后又有新的保存时写出来，重写取代原来的几件事", async ({ page }) => {
  const { backend, streamed, saveEpisode } = seed();
  const old = saveEpisode();
  old.recordedAt = "2020-01-01T00:00:00.000Z";
  await open(backend, page);
  await expect(page.getByText("之后有新的记录")).toBeVisible();
  backend.narrative = {
    events: [
      { event: "episode", data: { ...streamed, title: "重新写的一件事" } },
      { event: "saved", data: { observationIds: [] } },
    ],
  };
  await page.getByRole("button", { name: "重写" }).click();
  await expect(page.getByRole("region", { name: "几件事" })).toBeVisible();
  await expect.poll(() => backend.narratives.length).toBe(1);
});

test("点几件事里的应用打开档案，档案里写出来源和经历", async ({ page }) => {
  const { backend, saveEpisode } = seed();
  saveEpisode();
  await open(backend, page);
  await page
    .getByRole("region", { name: "应用" })
    .getByRole("button", { name: /Visual Studio Code/ })
    .click();
  await expect(page.getByRole("heading", { name: "Visual Studio Code" })).toBeVisible();
  await expect(page.getByRole("button", { name: /前台状态 Observer/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /1 份前台应用时段/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /讨论事物模型/ })).toBeVisible();
});

test("会话的档案按类型收起它的交互，点开逐条列出", async ({ page }) => {
  const { backend, talked } = seed();
  await backend.serve(page);
  await page.goto(`/things/${talked.thingId}`);
  await page.getByRole("button", { name: /份助手交互/ }).click();
  await expect(page.getByText("它的一部分")).toBeVisible();
});
