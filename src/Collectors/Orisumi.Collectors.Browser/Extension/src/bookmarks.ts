import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";

type Bookmark = {
  bookmarkId: string;
  parentId: string | null;
  position: number | null;
  title: string;
  url: string | null;
  addedAtUtcMs: number | null;
  lastUsedAtUtcMs: number | null;
};

type Change =
  | { kind: "snapshot"; nodes: Bookmark[] }
  | { kind: "created"; node: Bookmark }
  | { kind: "changed"; bookmarkId: string; title: string; url?: string }
  | { kind: "moved"; bookmarkId: string; parentId: string; position: number }
  | { kind: "removed"; bookmarkId: string }
  | { kind: "reordered"; bookmarkId: string; childIds: string[] };

export type BookmarkObservation = Change & {
  observationId: string;
  runId: string;
  runStartedAtUtcMs: number;
  runSequence: number;
  observedAtUtcMs: number;
};

type Run = { id: string; startedAtUtcMs: number };
type State = { runId?: string; sequence: number; pending: BookmarkObservation[] };

function bookmark(node: chrome.bookmarks.BookmarkTreeNode): Bookmark {
  return {
    bookmarkId: node.id, parentId: node.parentId ?? null, position: node.index ?? null,
    title: node.title, url: node.url ?? null,
    addedAtUtcMs: node.dateAdded == null ? null : Math.trunc(node.dateAdded),
    lastUsedAtUtcMs: node.dateLastUsed == null ? null : Math.trunc(node.dateLastUsed),
  };
}

export function observeBookmarks() {
  let operations: Promise<void> = Promise.resolve();
  let revision = 0;
  let flushing: Promise<void> | undefined;
  const startedAtUtcMs = Date.now();
  let run: Promise<Run> | undefined;

  function captureRun(): Promise<Run> {
    return run ??= (async () => {
      const { bookmarkCaptureRun } = await chrome.storage.session.get<{ bookmarkCaptureRun?: Run }>("bookmarkCaptureRun");
      if (bookmarkCaptureRun) return bookmarkCaptureRun;
      const created = { id: crypto.randomUUID(), startedAtUtcMs };
      await chrome.storage.session.set({ bookmarkCaptureRun: created });
      return created;
    })().catch(error => { run = undefined; throw error; });
  }

  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = operations.then(operation);
    operations = result.then(() => {}, error => console.error("Could not record bookmark change", error));
    return result;
  }

  async function readState(): Promise<State> {
    const { bookmarkCaptureState } = await chrome.storage.local.get<{ bookmarkCaptureState?: State }>("bookmarkCaptureState");
    return bookmarkCaptureState ?? { sequence: 0, pending: [] };
  }

  function record(change: Change, observedAtUtcMs = Date.now()) {
    return serialize(async () => {
      const currentRun = await captureRun();
      const state = await readState();
      const sequence = state.runId === currentRun.id ? state.sequence + 1 : 1;
      const observation: BookmarkObservation = {
        ...change, observationId: crypto.randomUUID(), runId: currentRun.id,
        runStartedAtUtcMs: currentRun.startedAtUtcMs, runSequence: sequence, observedAtUtcMs,
      };
      if (jsonBytes({ bookmarkObservation: observation }) > batchBytesAvailable)
        throw new Error("A bookmark observation exceeds the local collector message limit.");
      await chrome.storage.local.set({ bookmarkCaptureState: {
        runId: currentRun.id, sequence, pending: [...state.pending, observation],
      } });
    });
  }

  function receive(change: Change) {
    revision++;
    void record(change).then(() => flush()).catch(error => console.error("Could not collect bookmark change", error));
  }

  chrome.bookmarks.onCreated.addListener((_id, node) => receive({ kind: "created", node: bookmark(node) }));
  chrome.bookmarks.onChanged.addListener((bookmarkId, change) => receive({ kind: "changed", bookmarkId, ...change }));
  chrome.bookmarks.onMoved.addListener((bookmarkId, info) => receive({ kind: "moved", bookmarkId, parentId: info.parentId, position: info.index }));
  chrome.bookmarks.onRemoved.addListener(bookmarkId => receive({ kind: "removed", bookmarkId }));
  chrome.bookmarks.onChildrenReordered.addListener((bookmarkId, info) => receive({ kind: "reordered", bookmarkId, childIds: info.childIds }));

  async function snapshot() {
    // An event during getTree makes the snapshot's position in the event sequence
    // ambiguous. Keep the events and retry the snapshot instead of replaying stale state.
    for (let attempt = 0; attempt < 3; attempt++) {
      const before = revision;
      const roots = await chrome.bookmarks.getTree();
      if (before !== revision) continue;
      const nodes: Bookmark[] = [];
      const pending = [...roots];
      while (pending.length) {
        const node = pending.pop()!;
        nodes.push(bookmark(node));
        if (node.children) pending.push(...node.children);
      }
      await record({ kind: "snapshot", nodes });
      return;
    }
    // The next scheduled sync retries the baseline; event collection continues.
  }

  async function drain() {
    while (true) {
      const observation = (await serialize(readState)).pending[0];
      if (!observation) return;
      await sendBatch({ bookmarkObservation: observation });
      await serialize(async () => {
        const state = await readState();
        if (state.pending[0]?.observationId !== observation.observationId)
          throw new Error("Bookmark acknowledgement does not match the pending observation.");
        await chrome.storage.local.set({ bookmarkCaptureState: { ...state, pending: state.pending.slice(1) } });
      });
    }
  }

  function flush(): Promise<void> {
    return flushing ??= drain().finally(() => { flushing = undefined; });
  }

  return {
    async sync() {
      // Pending events are replayed before a new baseline, including after a lost
      // acknowledgement or database replacement. Accepted raw history stays in SQLite.
      await flush();
      await snapshot();
      await flush();
    },
    flush,
  };
}
