import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";

type Fields = {
  url?: string; finalUrl?: string | null; filename?: string | null;
  startedAtUtcMs?: number; endedAtUtcMs?: number | null;
  state?: chrome.downloads.DownloadItem["state"]; totalBytes?: number | null; mimeType?: string | null;
};
export type Download = {
  eventId: string; runId: string; runStartedAtUtcMs: number; runSequence: number;
  observedAtUtcMs: number; kind: "snapshot" | "created" | "changed"; downloadId: number; fields: Fields;
};
type State = { runId: string; sequence: number; pending: Download[]; privateIds: number[] };

export function observeDownloads() {
  let operations = Promise.resolve();
  let changes = 0;
  function serialize(operation: () => Promise<void>) {
    operations = operations.then(operation).catch(error => console.error("Could not record download", error));
    return operations;
  }
  async function state(): Promise<State> {
    return (await chrome.storage.local.get<{ downloadFacts?: State }>("downloadFacts")).downloadFacts ??
      { runId: "", sequence: 0, pending: [], privateIds: [] };
  }
  async function drain() {
    const current = await state();
    while (current.pending.length) {
      let count = 0, bytes = 0;
      while (count < current.pending.length) {
        const size = jsonBytes(current.pending[count]) + 1;
        if (bytes + size > batchBytesAvailable) break;
        bytes += size;
        count++;
      }
      if (!count) throw new Error("A download exceeds the local collector message limit.");
      await sendBatch({ downloads: current.pending.slice(0, count) });
      current.pending.splice(0, count);
      await chrome.storage.local.set({ downloadFacts: current });
    }
  }
  async function record(kind: Download["kind"], downloadId: number, fields: Fields, at: number, incognito?: boolean) {
    const current = await state();
    if (incognito || current.privateIds.includes(downloadId)) {
      if (!current.privateIds.includes(downloadId)) current.privateIds.push(downloadId);
      await chrome.storage.local.set({ downloadFacts: current });
      return;
    }
    let { downloadRun } = await chrome.storage.session.get<{ downloadRun?: { id: string; started: number } }>("downloadRun");
    if (!downloadRun) {
      downloadRun = { id: crypto.randomUUID(), started: at };
      await chrome.storage.session.set({ downloadRun });
    }
    current.sequence = current.runId === downloadRun.id ? current.sequence + 1 : 1;
    current.runId = downloadRun.id;
    current.pending.push({ eventId: crypto.randomUUID(), runId: downloadRun.id, runStartedAtUtcMs: downloadRun.started,
      runSequence: current.sequence, observedAtUtcMs: at, kind, downloadId, fields });
    await chrome.storage.local.set({ downloadFacts: current });
  }
  function fields(item: chrome.downloads.DownloadItem): Fields {
    return { url: item.url, finalUrl: item.finalUrl || null, filename: item.filename || null,
      startedAtUtcMs: Date.parse(item.startTime), endedAtUtcMs: item.endTime ? Date.parse(item.endTime) : null,
      state: item.state, totalBytes: item.totalBytes >= 0 ? item.totalBytes : null, mimeType: item.mime || null };
  }
  chrome.downloads.onCreated.addListener(item => {
    changes++;
    const at = Date.now();
    void serialize(async () => { await record("created", item.id, fields(item), at, item.incognito); await drain(); });
  });
  chrome.downloads.onChanged.addListener(delta => {
    changes++;
    const at = Date.now();
    // Keep the callback's values: a later search may already see the next state.
    const patch: Fields = {};
    if (delta.url) patch.url = delta.url.current;
    if (delta.finalUrl) patch.finalUrl = delta.finalUrl.current ?? null;
    if (delta.filename) patch.filename = delta.filename.current ?? null;
    if (delta.state?.current === "in_progress" || delta.state?.current === "interrupted" || delta.state?.current === "complete") patch.state = delta.state.current;
    if (delta.endTime) patch.endedAtUtcMs = delta.endTime.current ? Date.parse(delta.endTime.current) : null;
    if (delta.totalBytes) patch.totalBytes = (delta.totalBytes.current ?? -1) >= 0 ? delta.totalBytes.current : null;
    if (delta.mime) patch.mimeType = delta.mime.current ?? null;
    if (!Object.keys(patch).length) return;
    void serialize(async () => {
      const [item] = await chrome.downloads.search({ id: delta.id });
      // A removed item without a known privacy classification is not collected.
      if (!item) return;
      await record("changed", delta.id, patch, at, item.incognito);
      await drain();
    });
  });
  return {
    async sync() {
      await serialize(drain);
      const version = changes;
      const items = await chrome.downloads.search({ limit: 0 });
      const at = Date.now();
      await serialize(async () => {
        if (version !== changes) return;
        for (const item of items) await record("snapshot", item.id, fields(item), at, item.incognito);
        await drain();
      });
    },
  };
}
