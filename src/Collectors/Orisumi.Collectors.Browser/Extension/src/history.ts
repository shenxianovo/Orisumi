import { batchBytesAvailable, getDatabaseId, jsonBytes, sendBatch } from "./native-host.js";
import { coverage } from "./coverage.js";

export type Visit = {
  visitId: string;
  visitedAtUtcMs: number;
  url: string;
  transition: NonNullable<chrome.history.VisitItem["transition"]> | null;
  referringVisitId: string | null;
  isLocal: boolean | null;
};

export type HistoryPage = {
  eventId?: string;
  url: string;
  title: string;
  observedAtUtcMs: number;
};

type SyncState = {
  databaseId: string;
  nextStart: number;
  backfillBefore: number;
};

const hourMs = 3_600_000;
const weekMs = 7 * 24 * hourMs;
const overlapMs = 60_000;

async function* readPages(startTime: number, endTime: number): AsyncGenerator<chrome.history.HistoryItem> {
  const maxResults = 10_000;
  const pages = await chrome.history.search({ text: "", startTime, endTime, maxResults });
  if (pages.length >= maxResults) {
    const middle = Math.floor((startTime + endTime) / 2);
    if (middle === startTime) throw new Error("Chrome history exceeded the search limit.");
    yield* readPages(startTime, middle);
    yield* readPages(middle, endTime);
    return;
  }
  yield* pages;
}

async function* readVisits(startTime: number, endTime: number): AsyncGenerator<Visit> {
  for await (const { url } of readPages(startTime, endTime)) {
    if (!url) continue;
    for (const visit of await chrome.history.getVisits({ url })) {
      // search() selects URLs by their latest visit. Older visits of the same URL
      // would never be found by a past range, so ingest every retained visit here.
      if (visit.visitTime === undefined) continue;
      yield {
        visitId: visit.visitId,
        visitedAtUtcMs: Math.trunc(visit.visitTime),
        url,
        transition: visit.transition ?? null,
        referringVisitId: visit.referringVisitId && visit.referringVisitId !== "0" ? visit.referringVisitId : null,
        isLocal: visit.isLocal ?? null,
      };
    }
  }
}

async function syncRange(start: number, end: number): Promise<void> {
  await sendVisits(readVisits(start, end));
}

async function sendVisits(source: AsyncIterable<Visit> | Visit[]): Promise<void> {
  let visits: Visit[] = [];
  let bytes = 0;
  for await (const visit of source) {
    const size = jsonBytes(visit) + 1;
    if (size > batchBytesAvailable) throw new Error("A visit exceeds the local collector message limit.");
    if (bytes + size > batchBytesAvailable || visits.length === 1000) {
      await sendBatch({ visits });
      visits = [];
      bytes = 0;
    }
    visits.push(visit);
    bytes += size;
  }
  if (visits.length) await sendBatch({ visits });
}

async function syncPageRange(start: number, end: number): Promise<void> {
  let historyPages: HistoryPage[] = [];
  let bytes = 0;
  for await (const { url, title } of readPages(start, end)) {
    if (!url || !title?.trim()) continue;
    const page = { url, title, observedAtUtcMs: Date.now() };
    const size = jsonBytes(page) + 1;
    if (size > batchBytesAvailable) throw new Error("A history page exceeds the local collector message limit.");
    if (bytes + size > batchBytesAvailable || historyPages.length === 1000) {
      await sendBatch({ historyPages });
      historyPages = [];
      bytes = 0;
    }
    historyPages.push(page);
    bytes += size;
  }
  if (historyPages.length) await sendBatch({ historyPages });
}

export function syncHistory(): Promise<void> {
  return syncRanges("visitSyncState", syncRange);
}

export function syncHistoryPages(): Promise<void> {
  return syncRanges("historyPageSyncState", syncPageRange);
}

type PendingPage = { id: string; url: string; title?: string; observedAtUtcMs: number; visits: boolean };
export function observeHistory() {
  let operations: Promise<unknown> = Promise.resolve();
  let flushing: Promise<void> | undefined;
  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = operations.then(operation);
    operations = result.catch(() => {});
    return result;
  }
  async function pending(): Promise<PendingPage[]> {
    return (await chrome.storage.local.get<{ pendingHistoryPages?: PendingPage[] }>("pendingHistoryPages")).pendingHistoryPages ?? [];
  }
  function enqueue(url: string, title: string | undefined, visits: boolean) {
    const event = { id: crypto.randomUUID(), url, title, observedAtUtcMs: Date.now(), visits };
    void serialize(async () => {
      await chrome.storage.local.set({ pendingHistoryPages: [...await pending(), event] });
    }).catch(async error => {
      await coverage("history.events", "unavailable", "event_record_failed", "replay");
      throw error;
    }).then(flush).catch(error => console.error("Could not collect history event", error));
  }
  chrome.history.onVisited.addListener(item => { if (item.url) enqueue(item.url, item.title, true); });
  chrome.history.onVisitRemoved.addListener(() => {
    void coverage("history.events", "unavailable", "source_records_removed", "replay");
  });
  chrome.tabs.onUpdated.addListener((_id, change, tab) => {
    if (!tab.incognito && tab.url && change.title?.trim()) enqueue(tab.url, change.title, false);
  });
  async function drain() {
    while (true) {
      const entry = (await serialize(pending))[0];
      if (!entry) return;
      if (entry.visits) {
        let retained: chrome.history.VisitItem[];
        try { retained = await chrome.history.getVisits({ url: entry.url }); }
        catch (error) { await coverage("history.events", "read_failed", "getVisits_failed", "replay"); throw error; }
        const visits = retained.filter(visit => visit.visitTime !== undefined)
          .map(visit => ({ visitId: visit.visitId, url: entry.url, visitedAtUtcMs: Math.trunc(visit.visitTime!),
            transition: visit.transition ?? null, referringVisitId: visit.referringVisitId === "0" ? null : visit.referringVisitId ?? null,
            isLocal: visit.isLocal ?? null }));
        if (!visits.length) await coverage("history.events", "unavailable", "visit_no_longer_available", "replay");
        await sendVisits(visits);
      }
      if (entry.title?.trim()) await sendBatch({ historyPages: [{ eventId: entry.visits ? undefined : entry.id,
        url: entry.url, title: entry.title, observedAtUtcMs: entry.observedAtUtcMs }] });
      await serialize(async () => {
        const entries = await pending(); entries.shift();
        await chrome.storage.local.set({ pendingHistoryPages: entries });
      });
    }
  }
  function flush(): Promise<void> { return flushing ??= drain().finally(() => { flushing = undefined; }); }
  return { flush };
}

async function syncRanges(key: "visitSyncState" | "historyPageSyncState",
  sync: (start: number, end: number) => Promise<void>): Promise<void> {
  const databaseId = await getDatabaseId();
  const previous = (await chrome.storage.local.get<Partial<Record<typeof key, SyncState>>>(key))[key];
  const now = Date.now();
  const state = previous?.databaseId === databaseId
    ? previous
    : { databaseId, nextStart: now, backfillBefore: now };
  if (state !== previous) await chrome.storage.local.set({ [key]: state });

  const start = Math.min(state.nextStart, now);
  const end = Math.min(start + hourMs, now);
  const scope = key === "visitSyncState" ? "history" : "history.titles";
  async function readRange(from: number, to: number) {
    try {
      await sync(from, to);
      await coverage(scope, "range_read", "URLs selected by latest visit; retained records only", "replay", from, to);
    } catch (error) {
      await coverage(scope, "read_failed", error instanceof Error ? error.message : String(error), "replay", from, to);
      throw error;
    }
  }
  await readRange(Math.max(0, start - overlapMs), end);
  state.nextStart = end;
  await chrome.storage.local.set({ [key]: state });

  const deadline = Date.now() + 25_000;
  while (state.backfillBefore > 0 && Date.now() < deadline) {
    let newest: chrome.history.HistoryItem | undefined;
    try {
      [newest] = await chrome.history.search({ text: "", startTime: 0, endTime: state.backfillBefore, maxResults: 1 });
    } catch (error) {
      await coverage(scope, "read_failed", "history_search_failed", "replay", 0, state.backfillBefore);
      throw error;
    }
    if (newest) {
      const end = Math.min(state.backfillBefore, Math.floor(newest.lastVisitTime ?? state.backfillBefore) + 1);
      const start = Math.max(0, end - weekMs);
      await readRange(start, end);
      state.backfillBefore = start;
    } else {
      state.backfillBefore = 0;
    }
    await chrome.storage.local.set({ [key]: state });
  }
}
