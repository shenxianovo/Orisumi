import assert from "node:assert/strict";
import test from "node:test";

// Exercise scheduling through real worker entry points while a history API call never completes.
test("history backfill does not block subsequent active-tab flushes", async () => {
  const local = {}, session = {}, listeners = {}, sent = [];
  const storage = data => ({ get: async keys => Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, structuredClone(data[key])])),
    set: async values => Object.assign(data, structuredClone(values)) });
  const subscriptions = {};
  const tabsEvents = ["onCreated", "onDetached", "onAttached", "onReplaced"];
  const event = name => ({ addListener: callback => { (subscriptions[name] ??= []).push(callback); listeners[name] = (...args) => subscriptions[name].forEach(listener => listener(...args)); } });
  let tab = { id: 1, windowId: 2, active: true, incognito: false, url: "https://example.com", title: "A" };
  const window = () => ({ id: 2, focused: true });
  globalThis.chrome = {
    storage: { local: storage(local), session: storage(session) },
    tabs: { ...Object.fromEntries(tabsEvents.map(name => [name, event(name)])), onActivated: event("activated"), onUpdated: event("updated"), onRemoved: event("tabRemoved"), get: async () => tab, query: async () => [structuredClone(tab)] },
    windows: { onCreated: event("windowCreated"), WINDOW_ID_NONE: -1, onFocusChanged: event("focus"), onRemoved: event("windowRemoved"), get: async () => window(), getAll: async () => [window()] },
    downloads: { onCreated: event("downloadCreated"), onChanged: event("downloadChanged"), onErased: event("downloadErased"), search: async () => [] },
    bookmarks: Object.fromEntries(["onCreated", "onChanged", "onMoved", "onRemoved", "onChildrenReordered"].map(name => [name, event(name)])),
    alarms: { get: async () => ({}), create: async () => {}, onAlarm: event("alarm") },
    history: { onVisited: event("visited"), onVisitRemoved: event("visitsRemoved"), search: async options => options.maxResults === 1 ? [] : [{ url: tab.url, title: "A", lastVisitTime: Date.now() }],
      getVisits: async () => new Promise(() => {}) },
    runtime: { onInstalled: event("installed"), onStartup: event("startup"),
      sendNativeMessage: async (_host, batch) => { if (batch.tabEvents) sent.push(...batch.tabEvents); return { ok: true, inserted: 1, databaseId: "test" }; } },
  };
  chrome.bookmarks.getTree = async () => [];
  const settle = async () => { for (let i=0;i<12;i++) await new Promise(resolve => setImmediate(resolve)); };
  await import("../../ChromeExtension/worker.js"); await settle();
  assert.equal(sent[0].tabs[0].fields.title, "A");
  tab = { ...tab, title: "B" }; listeners.updated(1, { title: "B" }, tab); await settle();
  listeners.alarm({ name: "sync-history" }); await settle();
  assert.equal(sent.at(-1).tabs[0].fields.title, "B");
});
