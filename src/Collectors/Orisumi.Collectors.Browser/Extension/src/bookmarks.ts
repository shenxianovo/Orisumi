import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";
import { coverage } from "./coverage.js";

type Bookmark = {
  bookmarkId: string; parentId: string | null; position: number | null; title: string; url: string | null;
  addedAtUtcMs: number | null; lastUsedAtUtcMs: number | null;
};
type Usage = { bookmarkId: string; lastUsedAtUtcMs: number };
type Change =
  | { kind: "snapshot"; nodes: Bookmark[] }
  | { kind: "last_used"; usages: Usage[] }
  | { kind: "created"; node: Bookmark }
  | { kind: "changed"; bookmarkId: string; title: string; url?: string }
  | { kind: "moved"; bookmarkId: string; parentId: string; position: number }
  | { kind: "removed"; bookmarkId: string }
  | { kind: "reordered"; bookmarkId: string; childIds: string[] };
export type BookmarkObservation = Change & {
  observationId: string; runId: string; runStartedAtUtcMs: number; runSequence: number; observedAtUtcMs: number;
};
type Run = { id: string; startedAtUtcMs: number };
type State = {
  runId?: string; sequence: number; pending: BookmarkObservation[];
  nodes?: Record<string, Bookmark>; baselineRun?: string; databaseId?: string; needsBaseline?: boolean;
};
const bookmark = (node: chrome.bookmarks.BookmarkTreeNode): Bookmark => ({
  bookmarkId: node.id, parentId: node.parentId ?? null, position: node.index ?? null,
  title: node.title, url: node.url ?? null, addedAtUtcMs: node.dateAdded == null ? null : Math.trunc(node.dateAdded),
  lastUsedAtUtcMs: node.dateLastUsed == null ? null : Math.trunc(node.dateLastUsed),
});

// This cache avoids sending a whole tree for a last-use sample. SQLite remains the fact store.
function apply(nodes: Record<string, Bookmark>, change: Change) {
  const shift = (parent: string | null, position: number | null, by: number, except?: string) => {
    if (position === null) return;
    for (const node of Object.values(nodes))
      if (node.bookmarkId !== except && node.parentId === parent && node.position !== null && node.position >= position) node.position += by;
  };
  switch (change.kind) {
    case "snapshot": {
      const incoming = Object.fromEntries(change.nodes.map(node => [node.bookmarkId, { ...node,
        addedAtUtcMs: node.addedAtUtcMs ?? nodes[node.bookmarkId]?.addedAtUtcMs ?? null,
        lastUsedAtUtcMs: node.lastUsedAtUtcMs ?? nodes[node.bookmarkId]?.lastUsedAtUtcMs ?? null }]));
      for (const key of Object.keys(nodes)) delete nodes[key];
      Object.assign(nodes, incoming); break;
    }
    case "created":
      if (!nodes[change.node.bookmarkId]) shift(change.node.parentId, change.node.position, 1);
      nodes[change.node.bookmarkId] = { ...change.node }; break;
    case "changed": {
      const node = nodes[change.bookmarkId];
      if (node) { node.title = change.title; if (change.url !== undefined) node.url = change.url; }
      break;
    }
    case "moved": {
      const node = nodes[change.bookmarkId];
      if (node) {
        shift(node.parentId, node.position === null ? null : node.position + 1, -1, node.bookmarkId);
        shift(change.parentId, change.position, 1, node.bookmarkId);
        node.parentId = change.parentId; node.position = change.position;
      }
      break;
    }
    case "removed": {
      const node = nodes[change.bookmarkId];
      if (node) shift(node.parentId, node.position === null ? null : node.position + 1, -1, node.bookmarkId);
      const removed = new Set([change.bookmarkId]);
      for (let size = 0; size !== removed.size;) {
        size = removed.size;
        for (const node of Object.values(nodes)) if (node.parentId && removed.has(node.parentId)) removed.add(node.bookmarkId);
      }
      for (const id of removed) delete nodes[id];
      break;
    }
    case "reordered":
      change.childIds.forEach((id, position) => { if (nodes[id]) { nodes[id].parentId = change.bookmarkId; nodes[id].position = position; } }); break;
    case "last_used":
      for (const usage of change.usages) if (nodes[usage.bookmarkId]) nodes[usage.bookmarkId].lastUsedAtUtcMs = usage.lastUsedAtUtcMs;
  }
}

export function observeBookmarks() {
  let operations: Promise<unknown> = Promise.resolve();
  let revision = 0, needsBaseline = false;
  let flushing: Promise<void> | undefined;
  const startedAtUtcMs = Date.now();
  async function captureRun(): Promise<Run> {
    const { bookmarkCaptureRun } = await chrome.storage.session.get<{ bookmarkCaptureRun?: Run }>("bookmarkCaptureRun");
    if (bookmarkCaptureRun) return bookmarkCaptureRun;
    const created = { id: crypto.randomUUID(), startedAtUtcMs };
    await chrome.storage.session.set({ bookmarkCaptureRun: created });
    return created;
  }
  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = operations.then(operation);
    operations = result.catch(() => {});
    return result;
  }
  async function readState(): Promise<State> {
    return (await chrome.storage.local.get<{ bookmarkCaptureState?: State }>("bookmarkCaptureState")).bookmarkCaptureState ?? { sequence: 0, pending: [] };
  }
  async function record(change: Change, at = Date.now()) {
    const currentRun = await captureRun(), state = await readState();
    const sequence = state.runId === currentRun.id ? state.sequence + 1 : 1;
    const observation: BookmarkObservation = { ...change, observationId: crypto.randomUUID(), runId: currentRun.id,
      runStartedAtUtcMs: currentRun.startedAtUtcMs, runSequence: sequence, observedAtUtcMs: Math.max(at, currentRun.startedAtUtcMs) };
    if (jsonBytes({ bookmarkObservation: observation }) > batchBytesAvailable) throw new Error("A bookmark observation exceeds the local collector message limit.");
    if (change.kind === "snapshot") state.nodes ??= {};
    if (state.nodes) apply(state.nodes, change);
    state.runId = currentRun.id; state.sequence = sequence; state.pending.push(observation);
    if (change.kind === "snapshot") { state.baselineRun = currentRun.id; state.needsBaseline = false; }
    await chrome.storage.local.set({ bookmarkCaptureState: state });
  }
  async function failed(detail: string) {
    needsBaseline = true;
    await serialize(async () => {
      const state = await readState(); state.needsBaseline = true;
      await chrome.storage.local.set({ bookmarkCaptureState: state });
    });
    await coverage("bookmarks", "unavailable", detail);
  }
  function receive(change: Change) {
    revision++;
    const at = Date.now();
    void serialize(() => record(change, at)).catch(async error => {
      await failed("event_record_failed");
      throw error;
    }).then(flush).catch(error => console.error("Could not collect bookmark change", error));
  }
  chrome.bookmarks.onCreated.addListener((_id, node) => receive({ kind: "created", node: bookmark(node) }));
  chrome.bookmarks.onChanged.addListener((bookmarkId, change) => receive({ kind: "changed", bookmarkId, ...change }));
  chrome.bookmarks.onMoved.addListener((bookmarkId, info) => receive({ kind: "moved", bookmarkId, parentId: info.parentId, position: info.index }));
  chrome.bookmarks.onRemoved.addListener(bookmarkId => receive({ kind: "removed", bookmarkId }));
  chrome.bookmarks.onChildrenReordered.addListener((bookmarkId, info) => receive({ kind: "reordered", bookmarkId, childIds: [...info.childIds] }));

  async function snapshot() {
    const started = Date.now();
    for (let attempt = 0; attempt < 3; attempt++) {
      const before = revision;
      const roots = await chrome.bookmarks.getTree();
      if (before !== revision) continue;
      const nodes: Bookmark[] = [], pending = [...roots];
      while (pending.length) { const node = pending.pop()!; nodes.push(bookmark(node)); if (node.children) pending.push(...node.children); }
      const accepted = await serialize(async () => {
        if (before !== revision) return false;
        const currentRun = await captureRun(), state = await readState();
        const baseline = needsBaseline || state.needsBaseline || state.baselineRun !== currentRun.id || !state.nodes;
        const structuralChange = !!state.nodes && (Object.keys(state.nodes).length !== nodes.length || nodes.some(node => {
          const old = state.nodes![node.bookmarkId];
          return !old || node.title !== old.title || node.url !== old.url || node.parentId !== old.parentId || node.position !== old.position;
        }));
        if (baseline || structuralChange) {
          if (state.baselineRun) {
            await coverage("bookmarks", "unavailable", baseline ? "baseline_reset" : "reconciliation_changed");
            if (state.baselineRun === currentRun.id)
              await chrome.storage.session.set({ bookmarkCaptureRun: { id: crypto.randomUUID(), startedAtUtcMs: Date.now() } });
          }
          await record({ kind: "snapshot", nodes });
          needsBaseline = false;
        } else {
          const usages = nodes.filter(node => node.lastUsedAtUtcMs !== null && node.lastUsedAtUtcMs !== state.nodes![node.bookmarkId]?.lastUsedAtUtcMs)
            .map(node => ({ bookmarkId: node.bookmarkId, lastUsedAtUtcMs: node.lastUsedAtUtcMs! }));
          if (usages.length) await record({ kind: "last_used", usages });
        }
        return true;
      });
      if (!accepted) continue;
      await flush();
      await coverage("bookmarks", "available", "baseline_received");
      await coverage("bookmarks.last_used", "sampled", null, "snapshot", started, Date.now());
      return;
    }
    await failed("snapshot_raced");
  }
  async function drain() {
    while (true) {
      const observation = (await serialize(readState)).pending[0];
      if (!observation) return;
      await sendBatch({ bookmarkObservation: observation });
      await serialize(async () => {
        const state = await readState();
        state.pending.shift();
        await chrome.storage.local.set({ bookmarkCaptureState: state });
      });
    }
  }
  function flush(): Promise<void> { return flushing ??= drain().finally(() => { flushing = undefined; }); }
  return {
    async sync(databaseId?: string) {
      await flush();
      await serialize(async () => {
        const state = await readState();
        if (databaseId && state.databaseId !== databaseId) {
          if (state.databaseId) await coverage("bookmarks", "unavailable", "database_replaced");
          await chrome.storage.session.set({ bookmarkCaptureRun: { id: crypto.randomUUID(), startedAtUtcMs: Date.now() } });
          state.databaseId = databaseId; state.baselineRun = undefined;
          await chrome.storage.local.set({ bookmarkCaptureState: state });
        }
      });
      try { await snapshot(); }
      catch (error) { await failed("snapshot_failed"); throw error; }
    },
    flush,
  };
}
