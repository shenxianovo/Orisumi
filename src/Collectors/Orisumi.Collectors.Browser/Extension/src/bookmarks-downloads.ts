import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";

export type Bookmark = {
  bookmarkId: string;
  parentId: string | null;
  position: number | null;
  title: string;
  url: string | null;
  addedAtUtcMs: number | null;
  lastUsedAtUtcMs: number | null;
};

export type Download = {
  downloadId: number;
  url: string;
  finalUrl: string | null;
  filename: string | null;
  startedAtUtcMs: number;
  endedAtUtcMs: number | null;
  state: chrome.downloads.DownloadItem["state"];
  totalBytes: number | null;
  mimeType: string | null;
};

export async function syncBookmarks(): Promise<void> {
  const bookmarks: Bookmark[] = [];
  const roots = await chrome.bookmarks.getTree();
  const pending = [...roots];
  let node: chrome.bookmarks.BookmarkTreeNode | undefined;
  while ((node = pending.pop()) !== undefined) {
    bookmarks.push({
      bookmarkId: node.id,
      parentId: node.parentId ?? null,
      position: node.index ?? null,
      title: node.title,
      url: node.url ?? null,
      addedAtUtcMs: node.dateAdded == null ? null : Math.trunc(node.dateAdded),
      lastUsedAtUtcMs: node.dateLastUsed == null ? null : Math.trunc(node.dateLastUsed),
    });
    for (const child of node.children ?? []) pending.push(child);
  }

  const snapshot = { bookmarks };
  if (jsonBytes(snapshot) > batchBytesAvailable) {
    throw new Error("The Chrome bookmark tree exceeds the local collector message limit.");
  }
  await sendBatch(snapshot);
}

export async function syncDownloads(): Promise<void> {
  const items = await chrome.downloads.search({ limit: 0 });
  let downloads: Download[] = [];
  let bytes = 0;
  for (const item of items) {
    if (item.incognito) continue;
    const download: Download = {
      downloadId: item.id,
      url: item.url,
      finalUrl: item.finalUrl || null,
      filename: item.filename || null,
      startedAtUtcMs: Date.parse(item.startTime),
      endedAtUtcMs: item.endTime ? Date.parse(item.endTime) : null,
      state: item.state,
      totalBytes: item.totalBytes >= 0 ? item.totalBytes : null,
      mimeType: item.mime || null,
    };
    const size = jsonBytes(download) + 1;
    if (size > batchBytesAvailable) throw new Error("A download exceeds the local collector message limit.");
    if (bytes + size > batchBytesAvailable) {
      await sendBatch({ downloads });
      downloads = [];
      bytes = 0;
    }
    downloads.push(download);
    bytes += size;
  }
  if (downloads.length) await sendBatch({ downloads });
}
