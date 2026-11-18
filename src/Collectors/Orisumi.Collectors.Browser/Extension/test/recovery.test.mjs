import assert from "node:assert/strict";
import test from "node:test";
import { observeTabs } from "../../ChromeExtension/tabs.js";
import { observeBookmarks } from "../../ChromeExtension/bookmarks.js";
import { observeDownloads } from "../../ChromeExtension/downloads.js";
import { observeHistory, syncHistory } from "../../ChromeExtension/history.js";
import { flushCoverage } from "../../ChromeExtension/coverage.js";

function browser() {
  const local = {}, session = {}, listeners = {}, sent = [];
  const storage = data => ({ get: async keys => Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, structuredClone(data[key])])),
    set: async values => Object.assign(data, structuredClone(values)) });
  const tabsEvents = ["onCreated", "onDetached", "onAttached", "onReplaced"];
  const event = name => ({ addListener: callback => { (listeners[name] ??= []).push(callback); } });
  const fixture = { local, session, sent, databaseId: "one", emit: (name, ...args) => listeners[name]?.forEach(callback => callback(...args)),
    tab: { id: 1, windowId: 2, active: true, incognito: false, url: "https://example.com", title: "A" },
    nodes: [{ id: "1", parentId: "0", index: 0, title: "A", url: "https://example.com" }], items: [] };
  globalThis.chrome = {
    storage: { local: storage(local), session: storage(session) },
    tabs: { ...Object.fromEntries(tabsEvents.map(name => [name, event(name)])), onActivated: event("activated"), onUpdated: event("updated"), onRemoved: event("removed"), get: async () => structuredClone(fixture.tab), query: async () => [structuredClone(fixture.tab)] },
    windows: { onCreated: event("windowCreated"), WINDOW_ID_NONE: -1, onFocusChanged: event("focus"), onRemoved: event("windowRemoved"),
      get: async () => ({ focused: true, tabs: [structuredClone(fixture.tab)] }), getAll: async () => [{ id: 2, focused: true }] },
    bookmarks: { getTree: async () => structuredClone(fixture.nodes),
      ...Object.fromEntries(["onCreated", "onChanged", "onMoved", "onRemoved", "onChildrenReordered"].map(name => [name, event(name)])) },
    downloads: { onCreated: event("downloadCreated"), onChanged: event("downloadChanged"), onErased: event("downloadErased"), search: async () => structuredClone(fixture.items) },
    history: { onVisited: event("visited"), onVisitRemoved: event("visitRemoved"), getVisits: async () => [], search: async () => [] },
    runtime: { sendNativeMessage: async (_host, request) => {
      if (request.getDatabaseId) return { ok: true, databaseId: fixture.databaseId };
      sent.push(structuredClone(request)); return { ok: true, inserted: 1 };
    } },
  };
  return fixture;
}
const settle = async () => { for (let i=0;i<10;i++) await new Promise(resolve => setImmediate(resolve)); };
const downloadsItem = { id: 8, url: "https://example.com/file", startTime: "2026-01-01T00:00:00Z", filename: "/tmp/a",
  state: "in_progress", totalBytes: 10, mime: "text/plain", incognito: false };

test("bookmarks send nothing while the tree is unchanged; worker wake keeps the run", async () => {
  const f = browser();
  let collector = observeBookmarks();
  await collector.sync("one");
  await collector.sync("one");
  collector = observeBookmarks();
  await collector.sync("one");
  let facts = f.sent.flatMap(batch => batch.bookmarkObservation ? [batch.bookmarkObservation] : []);
  assert.equal(facts.length, 1);
  f.nodes[0].dateLastUsed = 12345;
  await collector.sync("one");
  facts = f.sent.flatMap(batch => batch.bookmarkObservation ? [batch.bookmarkObservation] : []);
  assert.deepEqual(facts.map(fact => fact.kind), ["snapshot", "snapshot"]);
  assert.equal(facts[0].runId, facts[1].runId);
  assert.equal(facts[1].nodes.find(node => node.bookmarkId === "1").lastUsedAtUtcMs, 12345);
  await flushCoverage();
});

test("receiver replacement establishes fresh baselines for tabs, bookmarks and downloads", async () => {
  const f = browser(); f.items = [downloadsItem];
  const tabs = observeTabs(), bookmarks = observeBookmarks(), downloads = observeDownloads();
  await tabs.sync("one"); await bookmarks.sync("one"); await downloads.sync("one");
  const old = { tabs: f.local.tabFacts.captureSessionId, bookmarks: f.local.bookmarkCaptureState.runId, downloads: f.local.downloadFacts.runId };
  await tabs.sync("two"); await bookmarks.sync("two"); await downloads.sync("two", false);
  assert.notEqual(f.local.tabFacts.captureSessionId, old.tabs);
  assert.notEqual(f.local.bookmarkCaptureState.runId, old.bookmarks);
  assert.notEqual(f.local.downloadFacts.runId, old.downloads);
  assert.equal(f.sent.flatMap(batch => batch.tabEvents ?? []).filter(event => event.eventKind === "snapshot").length, 2);
  assert.equal(f.sent.filter(batch => batch.bookmarkObservation?.kind === "snapshot").length, 2);
  assert.equal(f.sent.flatMap(batch => batch.downloads ?? []).filter(event => event.kind === "snapshot").length, 2);
  await flushCoverage();
});

test("read failures survive worker wake and rebuild unchanged baselines in new runs", async () => {
  const f = browser(); f.items = [downloadsItem];
  let tabs = observeTabs(), bookmarks = observeBookmarks(), downloads = observeDownloads();
  await tabs.sync("one"); await bookmarks.sync("one"); await downloads.sync("one");
  const old = [f.local.tabFacts.captureSessionId, f.local.bookmarkCaptureState.runId, f.local.downloadFacts.runId];
  const all = chrome.tabs.query, tree = chrome.bookmarks.getTree, search = chrome.downloads.search;
  const fail = async () => { throw new Error("source unavailable"); };
  chrome.tabs.query = fail; chrome.bookmarks.getTree = fail; chrome.downloads.search = fail;
  await assert.rejects(tabs.sync("one"), /source unavailable/);
  await assert.rejects(bookmarks.sync("one"), /source unavailable/);
  await assert.rejects(downloads.sync("one"), /source unavailable/);
  chrome.tabs.query = all; chrome.bookmarks.getTree = tree; chrome.downloads.search = search;
  tabs = observeTabs(); bookmarks = observeBookmarks(); downloads = observeDownloads();
  await tabs.sync("one"); await bookmarks.sync("one"); await downloads.sync("one", false);
  const current = [f.local.tabFacts.captureSessionId, f.local.bookmarkCaptureState.runId, f.local.downloadFacts.runId];
  current.forEach((value, i) => assert.notEqual(value, old[i]));
  assert.equal(f.sent.flatMap(batch => batch.tabEvents ?? []).filter(event => event.eventKind === "snapshot").length, 2);
  assert.equal(f.sent.filter(batch => batch.bookmarkObservation?.kind === "snapshot").length, 2);
  assert.equal(f.sent.flatMap(batch => batch.downloads ?? []).filter(event => event.kind === "snapshot").length, 2);
  await flushCoverage();
});

test("database reset replays pending identities before the new baseline", async () => {
  const f = browser(); f.items = [downloadsItem];
  const tabs = observeTabs(), bookmarks = observeBookmarks(), downloads = observeDownloads();
  await tabs.sync("one"); await bookmarks.sync("one"); await downloads.sync("one");
  const sender = chrome.runtime.sendNativeMessage;
  chrome.runtime.sendNativeMessage = async () => { throw new Error("receiver offline"); };
  f.tab.title = "B"; f.nodes[0].title = "B"; f.items[0] = { ...downloadsItem, state: "complete" };
  f.emit("updated", 1, { title: "B" }, f.tab);
  f.emit("onChanged", "1", { title: "B" });
  f.emit("downloadChanged", { id: 8, state: { current: "complete" } });
  await settle();
  await assert.rejects(tabs.flush(), /receiver offline/);
  const ids = [f.local.tabFacts.pending[0].eventId, f.local.bookmarkCaptureState.pending[0].observationId, f.local.downloadFacts.pending[0].eventId];
  chrome.runtime.sendNativeMessage = sender;
  const start = f.sent.length;
  await tabs.sync("two"); await bookmarks.sync("two"); await downloads.sync("two", false);
  const replay = f.sent.slice(start);
  assert.equal(replay.flatMap(batch => batch.tabEvents ?? [])[0].eventId, ids[0]);
  assert.equal(replay.find(batch => batch.bookmarkObservation).bookmarkObservation.observationId, ids[1]);
  assert.equal(replay.flatMap(batch => batch.downloads ?? [])[0].eventId, ids[2]);
  await flushCoverage();
});

test("download list removal is an event and does not change file fields", async () => {
  const f = browser(); f.items = [downloadsItem];
  const downloads = observeDownloads(); await downloads.sync("one");
  f.items = []; f.emit("downloadErased", 8); await downloads.flush();
  const removed = f.sent.flatMap(batch => batch.downloads ?? []).at(-1);
  assert.equal(removed.kind, "erased");
  assert.deepEqual(removed.fields, {});
  assert.equal(f.local.downloadFacts.known[8].state, "in_progress");
  await flushCoverage();
});

test("history notification persists work and retains earlier visits of a recently visited URL", async () => {
  const f = browser(), collector = observeHistory();
  const old = Date.now() - 2 * 86400000, recent = Date.now() - 100;
  chrome.history.getVisits = async () => [{ visitId: "old", visitTime: old }, { visitId: "recent", visitTime: recent }];
  chrome.history.search = async options => options.startTime <= recent && recent < options.endTime ? [{ url: f.tab.url, lastVisitTime: recent }] : [];
  await syncHistory();
  assert.ok(f.sent.flatMap(batch => batch.visits ?? []).some(visit => visit.visitId === "old"));
  let fail = true;
  const send = chrome.runtime.sendNativeMessage;
  chrome.runtime.sendNativeMessage = async (...args) => { if (fail && args[1].visits) { fail = false; throw new Error("offline"); } return send(...args); };
  f.emit("visited", { url: f.tab.url, title: "B" });
  await assert.rejects(collector.flush(), /offline/);
  assert.equal(f.local.pendingHistoryPages.length, 1);
  await collector.flush();
  assert.equal(f.local.pendingHistoryPages.length, 0);
  assert.equal(f.sent.flatMap(batch => batch.historyPages ?? []).at(-1).title, "B");
  await flushCoverage();
});
