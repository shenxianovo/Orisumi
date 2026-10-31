import { expect, test } from "@playwright/test";
import { Backend, type Thing, type Version } from "./backend";

// 助手会话的档案：用内存里的后端放进一天的 Claude Code 和 Codex 交互，检查事物页的会话、
// 工具调用与结果的配对、视图的切换，以及只在原始记录中的工具输出。

const day = new Date();
const at = (hours: number, minutes: number, seconds = 0) =>
  new Date(day.getFullYear(), day.getMonth(), day.getDate(), hours, minutes, seconds).toISOString();

const thingRef = { type: "reference", target: "thing", description: "", required: true, nullable: false };

const empty = {
  client: null,
  sourceRole: null,
  sourceTurnId: null,
  sourceParentId: null,
  sourceMessageId: null,
  sourceVersion: null,
  callId: null,
  toolCallVersionId: null,
  toolName: null,
  toolServer: null,
  isError: null,
  exitCode: null,
  phase: null,
  model: null,
};

function seed() {
  const backend = new Backend();
  const observerSchema = backend.schema("assistant-sessions-observers", "助手会话 Observer", "observation");
  const sessionSchema = backend.schema("assistant-sessions", "助手会话", "thing");
  const interactionSchema = backend.schema("assistant-interactions", "助手交互", "observation", {
    sessionId: thingRef,
  });
  const subagentSchema = backend.schema("assistant-subagents", "子 Agent 关系", "observation", {
    sessionId: thingRef,
    parentSessionId: thingRef,
  });
  const observer = backend.program(observerSchema, { platform: "macos", programVersion: "0.1.0" });

  const session = (product: string, id: string, title: string | null) =>
    backend.thing(sessionSchema.entityId, { scope: null, scheme: `${product}.session`, value: id }, title);
  const interact = (session: Thing, startAt: string | null, endAt: string, content: Record<string, unknown>): Version =>
    backend.observe(
      interactionSchema,
      observer.thing,
      {
        ...empty,
        sessionId: session.entityId,
        sourceId: content.sourceId ?? `${session.entityId}-${endAt}`,
        workingDirectory: "/Users/me/Code/Heartbeat",
        parts: [],
        sourceEntries: [],
        ...content,
      },
      { startAt, endAt, observerVersionId: observer.description.versionId },
    );
  const text = (value: string, type = "text") => ({
    type,
    text: value,
    entryIndex: 0,
    pointer: "/message/content/0/text",
    mediaType: null,
    file: null,
  });

  const claude = session("claude-code", "c1", "讨论助手会话的建模");
  const codex = session("codex", "x1", "跑一下文档检查");
  const child = session("claude-code", "a1", null);
  backend.observe(subagentSchema, observer.thing, {
    agentName: "web-fetch",
    spawnCallId: null,
    sessionId: child.entityId,
    parentSessionId: claude.entityId,
  });

  interact(claude, null, at(9, 0), { kind: "context", actor: "application", sourceKind: "attachment/instructions" });
  interact(claude, at(9, 1), at(9, 1), {
    kind: "message",
    actor: "user",
    sourceKind: "user",
    parts: [text("能不能把粒度细到一次 query？")],
  });
  interact(claude, null, at(9, 1, 20), {
    kind: "reasoning",
    actor: "assistant",
    sourceKind: "thinking",
    parts: [text("先看看两边的日志结构")],
  });
  interact(claude, null, at(9, 2), {
    kind: "message",
    actor: "assistant",
    sourceKind: "text",
    model: "claude-opus-5-5",
    parts: [text("可以，而且时间问题会简单很多。")],
  });
  interact(child, at(9, 1, 30), at(9, 1, 30), {
    kind: "message",
    actor: "agent",
    sourceKind: "user",
    parts: [text("读这篇文章并总结")],
  });

  const output = backend.raw({ payload: { item: { aggregated_output: "Checked 154 documents and 240 sources.\n" } } });
  interact(codex, at(10, 0), at(10, 0), {
    kind: "message",
    actor: "user",
    sourceKind: "UserMessage",
    parts: [text("跑下文档检查")],
  });
  const call = interact(codex, at(10, 0, 5), at(10, 0, 5), {
    kind: "tool-call",
    actor: "assistant",
    sourceKind: "CommandExecution",
    callId: "exec-1",
    parts: [{ ...text('["/bin/zsh","-lc","pnpm docs:check"]', "json"), pointer: "/payload/item/command" }],
    sourceEntries: [output],
  });
  interact(codex, at(10, 0, 9), at(10, 0, 9), {
    kind: "tool-result",
    actor: "tool",
    sourceKind: "CommandExecution",
    callId: "exec-1",
    toolCallVersionId: call.versionId,
    isError: false,
    exitCode: 0,
    parts: [{ ...text("", "text"), text: null, pointer: "/payload/item/aggregated_output" }],
    sourceEntries: [output],
  });
  interact(codex, at(10, 1), at(10, 1, 3), {
    kind: "message",
    actor: "assistant",
    sourceKind: "AgentMessage",
    phase: "final_answer",
    parts: [text("检查通过了。")],
  });
  return { backend, claude, codex, child };
}

test("事物页列出这一天的会话，点一个打开它的档案和对话", async ({ page }) => {
  const { backend } = seed();
  await backend.serve(page);
  await page.goto("/things");
  const sessions = page.getByRole("region", { name: "助手会话" }).getByRole("link");
  await expect(sessions).toHaveCount(3);
  await page.getByRole("link", { name: /讨论助手会话的建模/ }).click();
  await expect(page.getByRole("heading", { name: "讨论助手会话的建模" })).toBeVisible();
  await expect(page.getByText("可以，而且时间问题会简单很多。")).toBeVisible();
});

test("子 Agent 的会话写出父会话", async ({ page }) => {
  const { backend, child } = seed();
  await backend.serve(page);
  await page.goto(`/things/${child.entityId}`);
  await expect(page.getByText("子 Agent，父会话")).toBeVisible();
  await expect(page.getByRole("button", { name: /讨论助手会话的建模/ })).toBeVisible();
});

test("对话视图只有消息，过程视图把工具调用和结果合成一行", async ({ page }) => {
  const { backend, codex } = seed();
  await backend.serve(page);
  await page.goto(`/things/${codex.entityId}`);
  await expect(page.getByText("跑下文档检查")).toBeVisible();
  await expect(page.getByText("检查通过了。")).toBeVisible();
  await expect(page.getByRole("button", { name: /CommandExecution/ })).toHaveCount(0);

  await page.getByRole("button", { name: "过程" }).click();
  const tool = page.getByRole("button", { name: /CommandExecution/ });
  await expect(tool).toHaveCount(1);
  await expect(tool).toContainText("/bin/zsh -lc pnpm docs:check");
  await tool.click();
  await expect(page.getByText("Checked 154 documents and 240 sources.")).toBeVisible();
});

test("全部视图显示上下文，推理默认收起", async ({ page }) => {
  const { backend, claude } = seed();
  await backend.serve(page);
  await page.goto(`/things/${claude.entityId}`);
  await expect(page.getByText("可以，而且时间问题会简单很多。")).toBeVisible();
  await page.getByRole("button", { name: "全部" }).click();
  await expect(page.getByRole("button", { name: /上下文 instructions/ })).toBeVisible();
  const reasoning = page.getByRole("button", { name: /推理/ });
  await expect(reasoning).toHaveAttribute("aria-expanded", "false");
  await expect(page).toHaveURL(/view=all/);
});
