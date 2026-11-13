import { batchBytesAvailable, getDatabaseId, jsonBytes, sendBatch } from "./native-host.js";

export type Visit = {
  visitId: string;
  visitedAtUtcMs: number;
  url: string;
  transition: NonNullable<chrome.history.VisitItem["transition"]> | null;
  referringVisitId: string | null;
  isLocal: boolean | null;
};

export type HistoryPage = {
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
      if (visit.visitTime === undefined || visit.visitTime < startTime || visit.visitTime >= endTime) continue;
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
  let visits: Visit[] = [];
  let bytes = 0;
  for await (const visit of readVisits(start, end)) {
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
  await sync(Math.max(0, start - overlapMs), end);
  state.nextStart = end;
  await chrome.storage.local.set({ [key]: state });

  const deadline = Date.now() + 25_000;
  while (state.backfillBefore > 0 && Date.now() < deadline) {
    const [newest] = await chrome.history.search({
      text: "", startTime: 0, endTime: state.backfillBefore, maxResults: 1,
    });
    if (newest) {
      const end = Math.min(state.backfillBefore, Math.floor(newest.lastVisitTime ?? state.backfillBefore) + 1);
      const start = Math.max(0, end - weekMs);
      await sync(start, end);
      state.backfillBefore = start;
    } else {
      state.backfillBefore = 0;
    }
    await chrome.storage.local.set({ [key]: state });
  }
}
