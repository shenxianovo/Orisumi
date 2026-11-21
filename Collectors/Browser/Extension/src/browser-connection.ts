import { readHistory, type Range } from "./history.js";
import { openOutbox, type Scope } from "./outbox.js";

type Reply = { ok: boolean; sequence?: number; history?: Range; error?: string };

export const report = (error: unknown) => console.error("Orisumi browser capture", error);

function initialize() {
  return Promise.all([
    openOutbox(),
    chrome.storage.local.get<{ profileId?: string }>("profileId"),
    chrome.storage.session.get<{ browserSession?: string }>("browserSession"),
  ]).then(async ([outbox, { profileId }, { browserSession }]) => {
    if (!profileId) await chrome.storage.local.set({ profileId: profileId = crypto.randomUUID() });
    if (!browserSession) await chrome.storage.session.set({ browserSession: browserSession = crypto.randomUUID() });
    return { outbox, browserSession, profileId, queueId: await outbox.queueId() };
  });
}

export function browserConnection(onReady: () => void) {
  let identity: ReturnType<typeof initialize> | undefined;
  const loadIdentity = () => identity ??= initialize().catch(error => {
    identity = undefined;
    throw error;
  });
  let port: number | undefined;
  async function loadPort() {
    const config = await fetch(chrome.runtime.getURL("config.json"));
    if (!config.ok) throw new Error("Missing Orisumi extension configuration");
    const { instance, port } = await config.json();
    if (typeof instance !== "string" || !/^[a-z0-9_]+$/.test(instance) ||
        !Number.isInteger(port) || port < 1 || port > 65535)
      throw new Error("Invalid Orisumi WebSocket configuration");
    return port as number;
  }
  let wake = () => {};
  let poll = true;
  let readingHistory = false;
  let retryDelay = 1000;
  async function enqueue(scope: Scope, kind: string, data: unknown, observedAtUtcMs = Date.now()) {
    const { outbox, browserSession } = await loadIdentity();
    await outbox.add({ scope, kind, data, observedAtUtcMs, browserSession });
    wake();
  }
  async function history(range: Range) {
    readingHistory = true;
    try { await enqueue("history", "range", await readHistory(range)); }
    catch (error) { await enqueue("history", "read_failed", String(error)); }
    finally { readingHistory = false; wake(); }
  }
  async function connect() {
    const { outbox, browserSession, profileId, queueId } = await loadIdentity();
    const socket = new WebSocket(`ws://127.0.0.1:${port ??= await loadPort()}/`);
    let closed = false;
    let changed = true;
    let waiting: (() => void) | undefined;
    let response: ((reply: Reply) => void) | undefined;
    let disconnected: ((error: Error) => void) | undefined;
    wake = () => { changed = true; waiting?.(); };
    const ready = new Promise<void>((resolve, reject) => {
      socket.addEventListener("open", () => resolve(), { once: true });
      const offline = () => {
        if (closed) return;
        const error = new Error("Orisumi WebSocket disconnected");
        closed = true;
        reject(error);
        disconnected?.(error);
        wake();
      };
      socket.addEventListener("error", offline);
      socket.addEventListener("close", offline);
    });
    socket.addEventListener("message", ({ data }) => {
      if (closed) return;
      try {
        const message = JSON.parse(data);
        if (message?.type === "heartbeat") return;
        if (typeof message?.ok !== "boolean") throw new Error("Invalid Orisumi WebSocket reply");
        response?.(message);
      } catch (error) {
        disconnected?.(error instanceof Error ? error : new Error(String(error)));
      }
    });
    async function request(value: object) {
      if (closed) throw new Error("Orisumi WebSocket disconnected");
      const reply = await new Promise<Reply>((resolve, reject) => {
        response = resolve;
        disconnected = reject;
        socket.send(JSON.stringify(value));
      }).finally(() => { response = undefined; disconnected = undefined; });
      if (!reply.ok) throw new Error(reply.error ?? "Orisumi did not confirm the request");
      return reply;
    }
    try {
      await ready;
      await request({ type: "open", browserSession, profileId, queueId });
      onReady();
      poll = true;
      while (!closed) {
        changed = false;
        const entry = await outbox.first();
        if (entry) {
          const reply = await request({ type: "delivery", ...entry });
          if (reply.sequence !== entry.sequence) throw new Error("Orisumi confirmed a different sequence");
          // The host confirms both committed facts and durably recorded rejections.
          await outbox.remove(entry.sequence);
          retryDelay = 1000;
          if (reply.history && !readingHistory && !closed) void history(reply.history).catch(report);
        } else if (poll && !readingHistory) {
          poll = false;
          const reply = await request({ type: "history_poll" });
          if (reply.history && !closed) void history(reply.history).catch(report);
        } else if (!changed && !closed) {
          await new Promise<void>(resolve => { waiting = resolve; });
          waiting = undefined;
        }
      }
    } finally {
      closed = true;
      if (socket.readyState < WebSocket.CLOSING) socket.close();
    }
  }
  function start() {
    void connect().catch(report).finally(() => {
      setTimeout(start, retryDelay);
      retryDelay = Math.min(2 * retryDelay, 30_000);
    });
  }
  return { enqueue, start, pollHistory: () => { poll = true; wake(); } };
}
