import { describe, expect, it } from "vitest";
import type { Observation, Thing } from "@/api/types";
import { observation, thing } from "@/test/fixtures";
import {
  argumentSummary,
  buildItems,
  groupSessions,
  nestSessions,
  partText,
  resolvePointer,
  sessionTitle,
  toInteraction,
  type Interaction,
  type InteractionContent,
  type SessionMeta,
} from "./conversations";

function session(name: string | null = null): Thing {
  return thing({ name, keys: [{ scope: null, scheme: "claude-code.session", value: "s" }] });
}

function interaction(
  session: Thing,
  at: string,
  content: Partial<InteractionContent>,
  startAt: string | null = at,
): Interaction {
  return toInteraction(
    observation({
      recordedAt: at,
      startAt,
      endAt: at,
      timeZone: null,
      content: {
        sessionId: session.thingId,
        client: null,
        kind: "message",
        actor: "user",
        sourceRole: null,
        sourceKind: "user",
        sourceId: at,
        sourceTurnId: null,
        callId: null,
        toolCallId: null,
        toolName: null,
        toolServer: null,
        isError: null,
        exitCode: null,
        phase: null,
        model: null,
        workingDirectory: null,
        parts: [],
        sourceEntries: [],
        ...content,
      },
    }) as unknown as Observation<InteractionContent>,
  );
}

const index = (...sessions: Thing[]) => new Map(sessions.map((s) => [s.thingId, s]));
const none: SessionMeta = { parentThingId: null, agentName: null };

describe("groupSessions", () => {
  it("按会话分组，组内按时间排列，最近活动的会话在前", () => {
    const a = session("A");
    const b = session("B");
    const groups = groupSessions(
      [
        interaction(a, "2026-10-31T02:00:00Z", { workingDirectory: "/x/heartbeat" }),
        interaction(b, "2026-10-31T03:00:00Z", {}),
        interaction(a, "2026-10-31T01:00:00Z", { kind: "tool-call", actor: "assistant" }),
      ],
      index(a, b),
      new Map(),
    );
    expect(groups.map(sessionTitle)).toEqual(["B", "A"]);
    expect(groups[1]!.interactions.map((i) => i.observation.content.kind)).toEqual(["tool-call", "message"]);
    expect(groups[1]).toMatchObject({ userMessages: 1, toolCalls: 1, workingDirectory: "/x/heartbeat" });
  });

  it("Claude Code 的助手回复只有结束边界，按结束时刻排序", () => {
    const a = session();
    const groups = groupSessions(
      [
        interaction(a, "2026-10-31T01:00:05Z", { actor: "assistant" }, null),
        interaction(a, "2026-10-31T01:00:00Z", { actor: "user" }),
      ],
      index(a),
      new Map(),
    );
    expect(groups[0]!.interactions.map((i) => i.observation.content.actor)).toEqual(["user", "assistant"]);
  });

  it("子 Agent 的会话排在父会话后面", () => {
    const parent = session("父");
    const child = session();
    const other = session("其他");
    const metas = new Map([
      [parent.thingId, none],
      [child.thingId, { parentThingId: parent.thingId, agentName: "web-fetch" }],
      [other.thingId, none],
    ]);
    const groups = groupSessions(
      [
        interaction(parent, "2026-10-31T01:00:00Z", {}),
        interaction(other, "2026-10-31T02:00:00Z", {}),
        interaction(child, "2026-10-31T03:00:00Z", { actor: "agent" }),
      ],
      index(parent, child, other),
      metas,
    );
    expect(nestSessions(groups).map(({ group, depth }) => [sessionTitle(group), depth])).toEqual([
      ["其他", 0],
      ["父", 0],
      ["子 Agent · web-fetch", 1],
    ]);
  });
});

describe("buildItems", () => {
  const s = session();
  const call = interaction(s, "2026-10-31T01:00:00Z", { kind: "tool-call", actor: "assistant", callId: "c1" });
  const other = interaction(s, "2026-10-31T01:00:01Z", { kind: "tool-call", actor: "assistant", callId: "c2" });
  const result = interaction(s, "2026-10-31T01:00:02Z", {
    kind: "tool-result",
    actor: "tool",
    callId: "c1",
    toolCallId: call.observation.observationId,
  });
  const late = interaction(s, "2026-10-31T01:00:03Z", { kind: "tool-result", actor: "tool", callId: "c2" });
  const orphan = interaction(s, "2026-10-31T01:00:04Z", { kind: "tool-result", actor: "tool", callId: "c9" });
  const context = interaction(s, "2026-10-31T00:59:00Z", { kind: "context", actor: "application" });
  const message = interaction(s, "2026-10-31T00:59:30Z", {});

  it("读不到会话时写读不到的会话", () => {
    const a = session("A");
    const groups = groupSessions([interaction(a, "2026-10-31T01:00:00Z", {})], new Map(), new Map());
    expect(groups.map(sessionTitle)).toEqual(["读不到的会话"]);
  });

  it("并行的调用按标识配对结果，没有调用的结果单独成一项", () => {
    const items = buildItems([message, call, other, result, late, orphan], "process");
    expect(items).toHaveLength(4);
    expect(items[1]).toMatchObject({ type: "tool", call, result });
    expect(items[2]).toMatchObject({ type: "tool", call: other, result: late });
    expect(items[3]).toMatchObject({ type: "tool", call: null, result: orphan });
  });

  it("对话只留消息，全部才有上下文", () => {
    expect(buildItems([context, message, call, result], "dialog")).toHaveLength(1);
    expect(buildItems([context, message, call, result], "process")).toHaveLength(2);
    expect(buildItems([context, message, call, result], "all")).toHaveLength(3);
  });
});

describe("resolvePointer", () => {
  const line = { message: { content: [{ type: "image", source: { data: "AAAA" } }, { "a/b": { "c~d": 1 } }] } };
  it("按 RFC 6901 解析", () => {
    expect(resolvePointer(line, "/message/content/0/source/data")).toBe("AAAA");
    expect(resolvePointer(line, "/message/content/1/a~1b/c~0d")).toBe(1);
    expect(resolvePointer(line, "/message/missing/0")).toBeUndefined();
    expect(resolvePointer(line, "")).toBe(line);
  });
});

describe("argumentSummary", () => {
  const withArguments = (text: string) =>
    ({ parts: [{ type: "json", text, entryIndex: 0, pointer: "", mediaType: null, file: null }] }) as never;
  it("取命令或路径", () => {
    expect(argumentSummary(withArguments('{"command":"pnpm test","description":"跑测试"}'))).toBe("pnpm test");
    expect(argumentSummary(withArguments('["/bin/zsh","-lc","pwd"]'))).toBe("/bin/zsh -lc pwd");
    expect(argumentSummary(withArguments('{"file_path":"/a/b.ts"}'))).toBe("/a/b.ts");
  });
});

describe("partText", () => {
  it("文字块连成正文，其余按 JSON 缩进", () => {
    expect(partText([{ type: "text", text: "Plan shown" }])).toBe("Plan shown");
    expect(
      partText({
        content: [
          { type: "text", text: "a" },
          { type: "text", text: "b" },
        ],
      }),
    ).toBe("a\nb");
    expect(partText([{ type: "image", source: {} }])).toContain('"type": "image"');
    expect(partText("raw")).toBe("raw");
  });
});
