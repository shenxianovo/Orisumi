import { readHistory, type Range } from "./history.js";
import { openOutbox, type Scope } from "./outbox.js";

type Reply = { ok: boolean; sequence?: number; history?: Range; error?: string };
const room = 1024 * 1024; // UTF-16 units. Even JSON-escaped chunks fit Chrome's 64 MiB input limit.

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

export function nativeHost(onReady: () => void) {
  let identity: ReturnType<typeof initialize> | undefined;
  const loadIdentity = () => identity ??= initialize().catch(error => {
    identity = undefined;
    throw error;
  });
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
    const port = chrome.runtime.connectNative("com.orisumi.browser");
    let closed = false;
    let changed = true;
    let waiting: (() => void) | undefined;
    let response: ((reply: Reply) => void) | undefined;
    let disconnected: ((error: Error) => void) | undefined;
    wake = () => { changed = true; waiting?.(); };
    port.onMessage.addListener(reply => { if (!closed) response?.(reply); });
    port.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError?.message ?? "Native port disconnected";
      closed = true;
      disconnected?.(new Error(error));
      wake();
    });
    async function request(send: () => void) {
      if (closed) throw new Error("Native port disconnected");
      const reply = await new Promise<Reply>((resolve, reject) => {
        response = resolve;
        disconnected = reject;
        send();
      }).finally(() => { response = undefined; disconnected = undefined; });
      if (!reply.ok) throw new Error(reply.error ?? "Native host did not confirm the request");
      return reply;
    }
    try {
      await request(() => port.postMessage({ type: "open", browserSession, profileId, queueId }));
      onReady();
      poll = true;
      while (!closed) {
        changed = false;
        const entry = await outbox.first();
        if (entry) {
          const delivery = { type: "delivery", ...entry };
          const encoded = JSON.stringify(delivery);
          const reply = await request(() => {
            if (encoded.length <= room) {
              port.postMessage(delivery);
              return;
            }
            for (let start = 0; start < encoded.length;) {
              let end = Math.min(start + room, encoded.length);
              const last = encoded.charCodeAt(end - 1);
              if (last >= 0xD800 && last <= 0xDBFF) end--;
              port.postMessage({ type: "part", body: encoded.slice(start, end), more: end < encoded.length });
              start = end;
            }
          });
          if (reply.sequence !== entry.sequence) throw new Error("Native host confirmed a different sequence");
          // The host confirms both committed facts and durably recorded rejections.
          await outbox.remove(entry.sequence);
          retryDelay = 1000;
          if (reply.history && !readingHistory && !closed) void history(reply.history).catch(report);
        } else if (poll && !readingHistory) {
          poll = false;
          const reply = await request(() => port.postMessage({ type: "history_poll" }));
          if (reply.history && !closed) void history(reply.history).catch(report);
        } else if (!changed && !closed) {
          await new Promise<void>(resolve => { waiting = resolve; });
          waiting = undefined;
        }
      }
    } finally {
      if (!closed) port.disconnect();
      closed = true;
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
