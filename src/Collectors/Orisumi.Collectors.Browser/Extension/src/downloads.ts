import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";
import { coverage } from "./coverage.js";

type Fields = {
  url?: string; finalUrl?: string | null; filename?: string | null;
  startedAtUtcMs?: number; endedAtUtcMs?: number | null;
  state?: chrome.downloads.DownloadItem["state"]; totalBytes?: number | null; mimeType?: string | null;
};
export type Download = {
  eventId: string; runId: string; runStartedAtUtcMs: number; runSequence: number;
  observedAtUtcMs: number; kind: "snapshot" | "created" | "changed" | "erased"; downloadId: number; fields: Fields;
};
type Run = { id: string; started: number };
type State = {
  runId: string; sequence: number; pending: Download[];
  privateIds: number[]; known: Record<number, Fields>; baselineRun?: string; databaseId?: string; needsBaseline?: boolean;
};

export function observeDownloads() {
  let operations: Promise<unknown> = Promise.resolve();
  let flushing: Promise<void> | undefined;
  let changes = 0;
  let needsBaseline = false;
  const same = (left: Fields | undefined, right: Fields) => !!left &&
    (Object.keys({ ...left, ...right }) as (keyof Fields)[]).every(key => left[key] === right[key]);
  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = operations.then(operation);
    operations = result.catch(() => {});
    return result;
  }
  async function state(): Promise<State> {
    const saved = (await chrome.storage.local.get<{ downloadFacts?: State }>("downloadFacts")).downloadFacts;
    return saved ?? { runId: "", sequence: 0, pending: [], privateIds: [], known: {} };
  }
  async function run(): Promise<Run> {
    let { downloadRun } = await chrome.storage.session.get<{ downloadRun?: Run }>("downloadRun");
    if (!downloadRun) {
      downloadRun = { id: crypto.randomUUID(), started: Date.now() };
      await chrome.storage.session.set({ downloadRun });
    }
    return downloadRun;
  }
  async function record(kind: Download["kind"], id: number, fields: Fields, at: number, incognito: boolean, force = false) {
    const current = await state();
    if (incognito || current.privateIds.includes(id)) {
      if (!current.privateIds.includes(id)) current.privateIds.push(id);
      delete current.known[id];
      await chrome.storage.local.set({ downloadFacts: current });
      return;
    }
    const next = { ...current.known[id], ...fields };
    if (kind === "snapshot" && !force && same(current.known[id], next)) return;
    const capture = await run();
    current.sequence = current.runId === capture.id ? current.sequence + 1 : 1;
    current.runId = capture.id;
    current.known[id] = next;
    current.pending.push({ eventId: crypto.randomUUID(), runId: capture.id, runStartedAtUtcMs: capture.started,
      runSequence: current.sequence, observedAtUtcMs: Math.max(at, capture.started), kind, downloadId: id, fields });
    await chrome.storage.local.set({ downloadFacts: current });
  }
  async function drain() {
    while (true) {
      const current = await serialize(state);
      if (!current.pending.length) return;
      let count = 0, bytes = 0;
      while (count < current.pending.length) {
        const size = jsonBytes(current.pending[count]) + 1;
        if (bytes + size > batchBytesAvailable) break;
        bytes += size; count++;
      }
      if (!count) throw new Error("A download exceeds the local collector message limit.");
      await sendBatch({ downloads: current.pending.slice(0, count) });
      await serialize(async () => {
        const latest = await state();
        latest.pending.splice(0, count);
        await chrome.storage.local.set({ downloadFacts: latest });
      });
    }
  }
  function flush(): Promise<void> { return flushing ??= drain().finally(() => { flushing = undefined; }); }
  async function failed(detail: string) {
    needsBaseline = true;
    await serialize(async () => {
      const current = await state(); current.needsBaseline = true;
      await chrome.storage.local.set({ downloadFacts: current });
    });
    await coverage("downloads", "unavailable", detail);
  }
  function received(operation: () => Promise<void>) {
    void serialize(operation).catch(async error => {
      await failed("event_record_failed");
      throw error;
    }).then(flush).catch(error => console.error("Could not collect download", error));
  }
  function fields(item: chrome.downloads.DownloadItem): Fields {
    return { url: item.url, finalUrl: item.finalUrl || null, filename: item.filename || null,
      startedAtUtcMs: Date.parse(item.startTime), endedAtUtcMs: item.endTime ? Date.parse(item.endTime) : null,
      state: item.state, totalBytes: item.totalBytes >= 0 ? item.totalBytes : null, mimeType: item.mime || null };
  }
  chrome.downloads.onCreated.addListener(item => {
    changes++;
    const at = Date.now(), captured = fields(item), incognito = item.incognito, downloadId = item.id;
    received(() => record("created", downloadId, captured, at, incognito));
  });
  chrome.downloads.onChanged.addListener(delta => {
    changes++;
    const at = Date.now(), downloadId = delta.id, patch: Fields = {};
    if (delta.url) patch.url = delta.url.current;
    if (delta.finalUrl) patch.finalUrl = delta.finalUrl.current ?? null;
    if (delta.filename) patch.filename = delta.filename.current ?? null;
    if (delta.state?.current === "in_progress" || delta.state?.current === "interrupted" || delta.state?.current === "complete") patch.state = delta.state.current;
    if (delta.endTime) patch.endedAtUtcMs = delta.endTime.current ? Date.parse(delta.endTime.current) : null;
    if (delta.totalBytes) patch.totalBytes = (delta.totalBytes.current ?? -1) >= 0 ? delta.totalBytes.current : null;
    if (delta.mime) patch.mimeType = delta.mime.current ?? null;
    if (!Object.keys(patch).length) return;
    received(async () => {
      const current = await state();
      if (current.privateIds.includes(downloadId)) return;
      if (current.known[downloadId]) {
        await record("changed", downloadId, patch, at, false);
      } else {
        const [item] = await chrome.downloads.search({ id: downloadId });
        if (!item) {
          needsBaseline = true; current.needsBaseline = true;
          await chrome.storage.local.set({ downloadFacts: current });
          await coverage("downloads", "unavailable", "download_privacy_unknown"); return;
        }
        await record("changed", downloadId, patch, at, item.incognito);
      }
    });
  });
  chrome.downloads.onErased.addListener(downloadId => {
    changes++;
    const at = Date.now();
    received(async () => {
      const current = await state();
      // This is removal from the browser list, not deletion of the file.
      if (current.known[downloadId]) await record("erased", downloadId, {}, at, false);
      else if (!current.privateIds.includes(downloadId))
        await coverage("downloads", "unavailable", "erased_download_privacy_unknown");
    });
  });
  return {
    flush,
    async sync(databaseId?: string, reconcile = true) {
      await flush();
      await serialize(async () => {
        const current = await state();
        if (databaseId && current.databaseId !== databaseId) {
          if (current.databaseId) await coverage("downloads", "unavailable", "database_replaced");
          const downloadRun = { id: crypto.randomUUID(), started: Date.now() };
          await chrome.storage.session.set({ downloadRun });
          current.databaseId = databaseId; current.baselineRun = undefined;
          await chrome.storage.local.set({ downloadFacts: current });
        }
      });
      let capture = await serialize(run);
      const before = await serialize(state);
      const baseline = needsBaseline || before.needsBaseline || before.baselineRun !== capture.id;
      if (!baseline && !reconcile) return;
      if (baseline && before.baselineRun) {
        await coverage("downloads", "unavailable", "baseline_reset");
        if (before.baselineRun === capture.id) {
          capture = { id: crypto.randomUUID(), started: Date.now() };
          await serialize(() => chrome.storage.session.set({ downloadRun: capture }));
        }
      }
      const version = changes;
      let items: chrome.downloads.DownloadItem[];
      try { items = await chrome.downloads.search({ limit: 0 }); }
      catch (error) { await failed("snapshot_failed"); throw error; }
      const at = Date.now();
      const accepted = await serialize(async () => {
        if (version !== changes) return false;
        const current = await state();
        const changed = !baseline && items.some(item => !item.incognito && !same(current.known[item.id], fields(item)));
        if (changed) {
          await coverage("downloads", "unavailable", "reconciliation_changed");
          capture = { id: crypto.randomUUID(), started: at };
          await chrome.storage.session.set({ downloadRun: capture });
        }
        for (const item of items) await record("snapshot", item.id, fields(item), at, item.incognito, baseline || changed);
        const saved = await state(); saved.baselineRun = capture.id; saved.needsBaseline = false;
        await chrome.storage.local.set({ downloadFacts: saved });
        return true;
      });
      await flush();
      if (accepted) { needsBaseline = false; await coverage("downloads", "available", "baseline_received"); }
      else await failed("snapshot_raced");
    },
  };
}
