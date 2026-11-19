import { nativeHost, report } from "./native-host.js";
import type { Scope } from "./outbox.js";

const alarm = "orisumi-capture";
const link = nativeHost(() => sample(true));
const none = chrome.windows.WINDOW_ID_NONE;
const windows = new Set<number>();
const tabs = new Set<number>();
const downloads = new Set<number>();
const generation = { tabs: 0, downloads: 0, bookmarks: 0 };
type SnapshotScope = keyof typeof generation;
let ordered: Promise<unknown> = Promise.resolve();

function send(scope: Scope, kind: string, data: unknown | Promise<unknown>) {
  const observedAt = Date.now();
  if (kind !== "snapshot" && scope !== "history") generation[scope]++;
  // Some ID-only callbacks need a privacy lookup. Keep their original place in the queue.
  const ready = Promise.resolve(data).catch(error => { report(error); return undefined; });
  ordered = ordered.then(async () => {
    const payload = await ready;
    if (payload !== undefined) await link.enqueue(scope, kind, payload, observedAt);
  }).catch(report);
}
const tabId = (tab: chrome.tabs.Tab) => tab.id !== undefined && tab.id >= 0 && !tab.incognito;
const windowId = (window: chrome.windows.Window) => window.id !== undefined && window.id >= 0 && !window.incognito;
const describeTab = ({ id, windowId, active, url, title }: chrome.tabs.Tab) => ({ id, windowId, active, url, title });
const describeWindow = ({ id, focused }: chrome.windows.Window) => ({ id, focused });

async function publicTab(id: number) {
  return tabs.has(id) || await chrome.tabs.get(id).then(tabId, () => false);
}
async function publicWindow(id: number) {
  return id !== none && (windows.has(id) || await chrome.windows.get(id).then(windowId, () => false));
}

chrome.windows.onCreated.addListener(window => {
  if (!windowId(window)) return;
  windows.add(window.id!);
  send("tabs", "window_created", describeWindow(window));
});
chrome.windows.onRemoved.addListener(windowId => {
  if (windows.delete(windowId)) send("tabs", "window_removed", { windowId });
});
chrome.windows.onFocusChanged.addListener(id => {
  send("tabs", "window_focus_changed", publicWindow(id).then(visible => ({ windowId: visible ? id : null })));
});
chrome.tabs.onCreated.addListener(tab => {
  if (!tabId(tab)) return;
  tabs.add(tab.id!);
  send("tabs", "tab_created", describeTab(tab));
});
chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
  if (tab.incognito || tabId < 0) return;
  tabs.add(tabId);
  if ("url" in change || "title" in change) send("tabs", "tab_updated", { tabId, change });
});
chrome.tabs.onActivated.addListener(info => {
  send("tabs", "tab_activated", publicTab(info.tabId).then(visible => visible ? info : undefined));
});
chrome.tabs.onRemoved.addListener((tabId, info) => {
  if (tabs.delete(tabId)) send("tabs", "tab_removed", { tabId, ...info });
});
chrome.tabs.onDetached.addListener((tabId, info) => {
  if (tabs.has(tabId)) send("tabs", "tab_detached", { tabId, ...info });
});
chrome.tabs.onAttached.addListener((tabId, info) => {
  send("tabs", "tab_attached", publicTab(tabId).then(visible => visible ? { tabId, ...info } : undefined));
});
chrome.tabs.onReplaced.addListener((addedTabId, removedTabId) => {
  const known = tabs.delete(removedTabId);
  send("tabs", "tab_replaced", chrome.tabs.get(addedTabId).then(tab => {
    if (!tabId(tab)) return undefined;
    tabs.add(addedTabId);
    return { addedTabId, removedTabId, tab: describeTab(tab) };
  }, () => known ? { addedTabId, removedTabId } : undefined));
});

chrome.downloads.onCreated.addListener(item => {
  if (item.incognito) return;
  downloads.add(item.id);
  send("downloads", "created", item);
});
chrome.downloads.onChanged.addListener(delta => {
  send("downloads", "changed", downloads.has(delta.id) ? delta :
    chrome.downloads.search({ id: delta.id }).then(([item]) => item && !item.incognito ? delta : undefined));
});
chrome.downloads.onErased.addListener(id => {
  if (downloads.delete(id)) send("downloads", "erased", { id });
});

function bookmark({ id, parentId, title, url, dateAdded, dateLastUsed }: chrome.bookmarks.BookmarkTreeNode) {
  return { id, parentId, title, url, dateAdded, dateLastUsed };
}
chrome.bookmarks.onCreated.addListener((id, node) => send("bookmarks", "created", { id, node: bookmark(node) }));
chrome.bookmarks.onChanged.addListener((id, change) => send("bookmarks", "changed", { id, change }));
chrome.bookmarks.onMoved.addListener((id, { parentId }) => send("bookmarks", "moved", { id, parentId }));
chrome.bookmarks.onRemoved.addListener(id => send("bookmarks", "removed", { id }));
chrome.history.onVisitRemoved.addListener(info => send("history", "removed", info));

const windowFilter = { populate: true, windowTypes: ["normal", "popup", "panel", "app", "devtools"] } satisfies chrome.windows.QueryOptions;
const readers = {
  tabs: () => chrome.windows.getAll(windowFilter),
  downloads: () => chrome.downloads.search({ limit: 0 }),
  bookmarks: () => chrome.bookmarks.getTree(),
};
function sampleData(scope: SnapshotScope, value: Awaited<ReturnType<typeof readers[SnapshotScope]>>) {
  if (scope === "tabs") {
    const normal = (value as chrome.windows.Window[]).filter(windowId);
    const open = normal.flatMap(window => window.tabs ?? []).filter(tabId);
    windows.clear();
    tabs.clear();
    normal.forEach(window => windows.add(window.id!));
    open.forEach(tab => tabs.add(tab.id!));
    return { windows: normal.map(describeWindow), tabs: open.map(describeTab),
      focusedWindowId: normal.find(window => window.focused)?.id ?? null };
  }
  if (scope === "downloads") {
    const normal = (value as chrome.downloads.DownloadItem[]).filter(item => !item.incognito);
    downloads.clear();
    normal.forEach(item => downloads.add(item.id));
    return normal;
  }
  const pending = [...value as chrome.bookmarks.BookmarkTreeNode[]];
  const nodes = [];
  while (pending.length) {
    const node = pending.pop()!;
    nodes.push(bookmark(node));
    if (node.children) pending.push(...node.children);
  }
  return nodes;
}
const sampling = new Set<SnapshotScope>();
async function snapshot(scope: SnapshotScope) {
  if (sampling.has(scope)) return;
  sampling.add(scope);
  try {
    for (let attempt = 0; attempt < 3; attempt++) {
      const before = generation[scope];
      const value = await readers[scope]();
      if (generation[scope] !== before) continue;
      send(scope, "snapshot", sampleData(scope, value));
      return;
    }
    throw new Error("Chrome changed during the snapshot");
  } catch (error) {
    send(scope, "read_failed", String(error));
  } finally {
    sampling.delete(scope);
  }
}

let lastSlow = 0;
function sample(force = false) {
  void snapshot("tabs");
  if (force || Date.now() - lastSlow >= 5 * 60_000) {
    lastSlow = Date.now();
    void snapshot("downloads");
    void snapshot("bookmarks");
  }
}
chrome.alarms.onAlarm.addListener(event => {
  if (event.name !== alarm) return;
  sample();
  link.pollHistory();
});
async function alarms() {
  if (!await chrome.alarms.get(alarm)) await chrome.alarms.create(alarm, { periodInMinutes: 1 });
}
let started = false;
function start() {
  if (started) return;
  started = true;
  void alarms().catch(report);
  link.start();
}
chrome.runtime.onInstalled.addListener(start);
chrome.runtime.onStartup.addListener(start);
start();
