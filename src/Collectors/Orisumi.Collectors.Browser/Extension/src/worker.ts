import { observeActiveTabs } from "./active-tabs.js";
import { syncBookmarks, syncDownloads } from "./bookmarks-downloads.js";
import { syncHistory } from "./history.js";

const alarm = "sync-history";
const activeTabs = observeActiveTabs();
let activeSync: Promise<void> | undefined;

async function sync() {
  const tasks = {
    history: syncHistory(),
    activeTabs: activeTabs.flush(),
    bookmarks: syncBookmarks(),
    downloads: syncDownloads(),
  };
  const results = await Promise.allSettled(Object.values(tasks));
  Object.keys(tasks).forEach((name, index) => {
    const result = results[index];
    if (result.status === "rejected") console.error(`Could not sync ${name}`, result.reason);
  });
}

function requestSync() {
  activeSync ??= sync().finally(() => { activeSync = undefined; });
  return activeSync;
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
void ensureAlarm().catch(error => console.error("Could not schedule browser collection", error));
