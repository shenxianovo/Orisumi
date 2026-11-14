import { observeActiveTabs } from "./active-tabs.js";
import { observeDownloads } from "./downloads.js";
import { observeBookmarks } from "./bookmarks.js";
import { observeHistory, syncHistory, syncHistoryPages } from "./history.js";
import { getDatabaseId } from "./native-host.js";
import { flushCoverage } from "./coverage.js";

const alarm = "sync-history";
const activeTabs = observeActiveTabs();
const bookmarks = observeBookmarks();
const downloads = observeDownloads();
const history = observeHistory();
const running = new Map<string, Promise<void>>();
let lastDownloadReconcile = 0;

async function requestSync(initial = false) {
  const databaseId = await getDatabaseId();
  const reconcileDownloads = initial || Date.now() - lastDownloadReconcile >= 5 * 60_000;
  const jobs: Record<string, () => Promise<void>> = {
    history: syncHistory,
    historyPages: syncHistoryPages,
    historyEvents: () => history.flush(),
    activeTabs: () => initial ? activeTabs.snapshot(databaseId) : activeTabs.sync(databaseId),
    bookmarks: () => bookmarks.sync(databaseId),
    downloads: async () => {
      await downloads.sync(databaseId, reconcileDownloads);
      if (reconcileDownloads) lastDownloadReconcile = Date.now();
    },
  };
  await Promise.all(Object.entries(jobs).map(([name, job]) => {
    let pending = running.get(name);
    if (!pending) {
      pending = job().catch(error => console.error(`Could not sync ${name}`, error))
        .finally(async () => {
          running.delete(name);
          await flushCoverage().catch(error => console.error("Could not save capture coverage", error));
        });
      running.set(name, pending);
    }
    return pending;
  }));
}

async function start() {
  if (!await chrome.alarms.get(alarm)) await chrome.alarms.create(alarm, { periodInMinutes: 1 });
  await requestSync(true);
}
function startInBackground() { void start().catch(error => console.error("Could not start the browser collector", error)); }
chrome.runtime.onInstalled.addListener(startInBackground);
chrome.runtime.onStartup.addListener(startInBackground);
chrome.alarms.onAlarm.addListener(event => {
  if (event.name === alarm) void requestSync().catch(error => console.error("Could not reach the local collector", error));
});
startInBackground();
