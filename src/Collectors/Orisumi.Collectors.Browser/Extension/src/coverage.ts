import { sendBatch } from "./native-host.js";

export type Coverage = {
  eventId: string;
  scope: string;
  mode: "notification" | "replay" | "snapshot";
  startedAtUtcMs: number;
  stoppedAtUtcMs: number | null;
  evidence: string;
  detail: string | null;
  runId: string | null;
};
type State = { pending: Coverage[]; latest: Record<string, string> };
let operations: Promise<unknown> = Promise.resolve();
function serialize<T>(operation: () => Promise<T>): Promise<T> {
  const result = operations.then(operation);
  operations = result.catch(() => {});
  return result;
}
async function read(): Promise<State> {
  return (await chrome.storage.local.get<{ captureCoverage?: State }>("captureCoverage")).captureCoverage ?? { pending: [], latest: {} };
}

export function coverage(scope: string, evidence: string, detail: string | null = null,
  mode: Coverage["mode"] = "notification", started = Date.now(), stopped: number | null = null): Promise<void> {
  return serialize(async () => {
    const state = await read();
    const key = `${scope}:${mode}`;
    let runId: string | null = null;
    if (scope === "active_tabs") runId = (await chrome.storage.session.get<{ captureSessionId?: string }>("captureSessionId")).captureSessionId ?? null;
    if (scope.startsWith("bookmarks")) runId = (await chrome.storage.session.get<{ bookmarkCaptureRun?: { id: string } }>("bookmarkCaptureRun")).bookmarkCaptureRun?.id ?? null;
    if (scope === "downloads") runId = (await chrome.storage.session.get<{ downloadRun?: { id: string } }>("downloadRun")).downloadRun?.id ?? null;
    const value = JSON.stringify([evidence, detail, runId]);
    if (stopped === null && state.latest[key] === value) return;
    if (stopped === null) state.latest[key] = value;
    state.pending.push({ eventId: crypto.randomUUID(), scope, mode, startedAtUtcMs: started,
      stoppedAtUtcMs: stopped, evidence, detail, runId });
    await chrome.storage.local.set({ captureCoverage: state });
  });
}

export function flushCoverage(): Promise<void> {
  return serialize(async () => {
    const state = await read();
    while (state.pending.length) {
      const batch = state.pending.slice(0, 256);
      await sendBatch({ coverage: batch });
      state.pending.splice(0, batch.length);
      await chrome.storage.local.set({ captureCoverage: state });
    }
  });
}
