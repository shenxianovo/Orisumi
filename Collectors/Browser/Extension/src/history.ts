export type Range = { direction: "forward" | "backfill"; cursor: number; startTime: number; endTime: number };

async function* search(startTime: number, endTime: number): AsyncGenerator<chrome.history.HistoryItem> {
  const maxResults = 10_000;
  const pages = await chrome.history.search({ text: "", startTime, endTime, maxResults });
  if (pages.length < maxResults) {
    yield* pages;
    return;
  }
  const middle = Math.floor((startTime + endTime) / 2);
  if (middle === startTime) throw new Error("Chrome history exceeded the search limit.");
  yield* search(startTime, middle);
  yield* search(middle, endTime);
}

export async function readHistory(range: Range) {
  let { startTime, endTime } = range;
  const pages = [];
  let hasPages = true;
  if (range.direction === "backfill") {
    const [newest] = await chrome.history.search({ text: "", startTime, endTime, maxResults: 1 });
    hasPages = Boolean(newest);
    if (newest) {
      const latest = Math.min(endTime, Math.floor(newest.lastVisitTime ?? endTime) + 1);
      startTime = Math.max(0, latest - 7 * 24 * 60 * 60_000);
      endTime = latest;
    }
  }
  if (hasPages)
    for await (const { url, title } of search(startTime, endTime)) {
      if (!url) continue;
      // Chrome searches by the URL's latest visit, so include all retained visits for each URL.
      const visits = (await chrome.history.getVisits({ url })).filter(visit => visit.visitTime !== undefined);
      pages.push({ url, title, visits });
    }
  return { ...range, startTime, pages };
}
