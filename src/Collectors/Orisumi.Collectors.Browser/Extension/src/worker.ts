import { observeActiveTabs } from "./active-tabs.js";
import { observeDownloads } from "./downloads.js";
import { observeBookmarks } from "./bookmarks.js";
import { syncHistory, syncHistoryPages } from "./history.js";

const alarm = "sync-history";
const activeTabs = observeActiveTabs();
const bookmarks = observeBookmarks();
const downloads = observeDownloads();
const jobs: Record<string, () => Promise<void>> = {
  history: syncHistory,
  historyPages: syncHistoryPages,
  activeTabs: () => activeTabs.flush(),
  bookmarks: () => bookmarks.sync(),
  downloads: () => downloads.sync(),
};
const running = new Map<string, Promise<void>>();

async function requestSync() {
  // A long history backfill must not hold up the other event queues.
  await Promise.all(Object.entries(jobs).map(([name, job]) => {
    let pending = running.get(name);
    if (!pending) {
      pending = job().catch(error => console.error(`Could not sync ${name}`, error))
        .finally(() => running.delete(name));
      running.set(name, pending);
    }
    return pending;
  }));
}

async function ensureAlarm() {
  if (!await chrome.alarms.get(alarm)) {
    await chrome.alarms.create(alarm, { periodInMinutes: 1 });
  }
}

async function start() {
  await ensureAlarm();
  try {
    await activeTabs.snapshot();
  } finally {
    await requestSync();
  }
}

function startInBackground() {
  void start().catch(error => console.error("Could not start the browser collector", error));
}

chrome.runtime.onInstalled.addListener(startInBackground);
chrome.runtime.onStartup.addListener(startInBackground);
chrome.alarms.onAlarm.addListener(event => {
  if (event.name === alarm) void requestSync();
});
startInBackground();
