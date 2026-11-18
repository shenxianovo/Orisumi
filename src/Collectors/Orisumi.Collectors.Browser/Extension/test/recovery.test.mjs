import assert from "node:assert/strict";
import test from "node:test";
import { observeActiveTabs } from "../../ChromeExtension/active-tabs.js";
import { observeBookmarks } from "../../ChromeExtension/bookmarks.js";
import { observeDownloads } from "../../ChromeExtension/downloads.js";
import { observeHistory, syncHistory } from "../../ChromeExtension/history.js";
import { flushCoverage } from "../../ChromeExtension/coverage.js";

function browser() {
  const local = {}, session = {}, listeners = {}, sent = [];
  const storage = data => ({ get: async keys => Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, structuredClone(data[key])])),
    set: async values => Object.assign(data, structuredClone(values)) });
  const event = name => ({ addListener: callback => { (listeners[name] ??= []).push(callback); } });
  const fixture = { local, session, sent, databaseId: "one", emit: (name, ...args) => listeners[name]?.forEach(callback => callback(...args)),
    tab: { id: 1, windowId: 2, active: true, incognito: false, url: "https://example.com", title: "A" },
    nodes: [{ id: "1", parentId: "0", index: 0, title: "A", url: "https://example.com" }], items: [] };
  globalThis.chrome = {
    storage: { local: storage(local), session: storage(session) },
    tabs: { onActivated: event("activated"), onUpdated: event("updated"), onRemoved: event("removed"), get: async () => structuredClone(fixture.tab) },
    windows: { WINDOW_ID_NONE: -1, onFocusChanged: event("focus"), onRemoved: event("windowRemoved"),
      get: async () => ({ focused: true, tabs: [structuredClone(fixture.tab)] }), getAll: async () => [{ focused: true, tabs: [structuredClone(fixture.tab)] }] },
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
  const tabs = observeActiveTabs(), bookmarks = observeBookmarks(), downloads = observeDownloads();
  await tabs.snapshot("one"); await bookmarks.sync("one"); await downloads.sync("one");
  const old = { tabs: f.local.activeTabFacts.captureSessionId, bookmarks: f.local.bookmarkCaptureState.runId, downloads: f.local.downloadFacts.runId };
  await tabs.sync("two"); await bookmarks.sync("two"); await downloads.sync("two", false);
  assert.notEqual(f.local.activeTabFacts.captureSessionId, old.tabs);
  assert.notEqual(f.local.bookmarkCaptureState.runId, old.bookmarks);
  assert.notEqual(f.local.downloadFacts.runId, old.downloads);
  assert.equal(f.sent.flatMap(batch => batch.activeTabEvents ?? []).filter(event => event.eventKind === "snapshot").length, 2);
  assert.equal(f.sent.filter(batch => batch.bookmarkObservation?.kind === "snapshot").length, 2);
  assert.equal(f.sent.flatMap(batch => batch.downloads ?? []).filter(event => event.kind === "snapshot").length, 2);
  await flushCoverage();
});

test("tab updates keep callback values, removal closes focus and read failures create a gap", async () => {
  const f = browser(), collector = observeActiveTabs();
  await collector.snapshot("one");
  let release;
  chrome.windows.get = () => new Promise(resolve => { release = resolve; });
  f.tab.title = "B"; f.emit("updated", 1, { title: "B" }, f.tab);
  f.tab.title = "A";
  release({ focused: true }); await collector.flush();
  let facts = f.sent.flatMap(batch => batch.activeTabEvents ?? []);
  assert.equal(facts.at(-1).title, "B");
  f.emit("removed", 1); await collector.flush();
  facts = f.sent.flatMap(batch => batch.activeTabEvents ?? []);
  assert.equal(facts.at(-1).tabId, null);
  chrome.windows.get = async () => { throw new Error("window disappeared"); };
  f.emit("updated", 1, { title: "A" }, f.tab); await settle();
  await collector.sync("one"); await flushCoverage();
  const evidence = f.sent.flatMap(batch => batch.coverage ?? []);
  assert.ok(evidence.some(value => value.evidence === "unavailable" && value.detail === "event_read_failed"));
  assert.equal(evidence.filter(value => value.scope === "active_tabs").at(-1).evidence, "available");
});

test("an event racing the initial tab query is not overwritten by its stale snapshot", async () => {
  const f = browser(), collector = observeActiveTabs();
  let reads = 0;
  chrome.windows.getAll = async () => {
    const stale = structuredClone(f.tab);
    if (++reads === 1) { f.tab.title = "B"; f.emit("updated", 1, { title: "B" }, f.tab); }
    return [{ focused: true, tabs: [stale] }];
  };
  await collector.snapshot("one");
  const facts = f.sent.flatMap(batch => batch.activeTabEvents ?? []);
  assert.ok(reads >= 2);
  assert.ok(facts.every(fact => fact.title === "B"));
  await flushCoverage();
});

test("closing a tab waits for the preceding asynchronous activation", async () => {
  const f = browser(), collector = observeActiveTabs();
  await collector.snapshot("one");
  f.tab.id = 3;
  let release;
  chrome.tabs.get = () => new Promise(resolve => { release = resolve; });
  f.emit("activated", { tabId: 3, windowId: 2 });
  f.emit("removed", 3);
  release(structuredClone(f.tab));
  await collector.flush();
  assert.deepEqual(f.sent.flatMap(batch => batch.activeTabEvents ?? []).map(value => value.tabId), [1, 3, null]);
  await flushCoverage();
});

test("read failures survive worker wake and rebuild unchanged baselines in new runs", async () => {
  const f = browser(); f.items = [downloadsItem];
  let tabs = observeActiveTabs(), bookmarks = observeBookmarks(), downloads = observeDownloads();
  await tabs.snapshot("one"); await bookmarks.sync("one"); await downloads.sync("one");
  const old = [f.local.activeTabFacts.captureSessionId, f.local.bookmarkCaptureState.runId, f.local.downloadFacts.runId];
  const all = chrome.windows.getAll, tree = chrome.bookmarks.getTree, search = chrome.downloads.search;
  const fail = async () => { throw new Error("source unavailable"); };
  chrome.windows.getAll = fail; chrome.bookmarks.getTree = fail; chrome.downloads.search = fail;
  await assert.rejects(tabs.snapshot("one"), /source unavailable/);
  await assert.rejects(bookmarks.sync("one"), /source unavailable/);
  await assert.rejects(downloads.sync("one"), /source unavailable/);
  chrome.windows.getAll = all; chrome.bookmarks.getTree = tree; chrome.downloads.search = search;
  tabs = observeActiveTabs(); bookmarks = observeBookmarks(); downloads = observeDownloads();
  await tabs.sync("one"); await bookmarks.sync("one"); await downloads.sync("one", false);
  const current = [f.local.activeTabFacts.captureSessionId, f.local.bookmarkCaptureState.runId, f.local.downloadFacts.runId];
  current.forEach((value, i) => assert.notEqual(value, old[i]));
  assert.equal(f.sent.flatMap(batch => batch.activeTabEvents ?? []).filter(event => event.eventKind === "snapshot").length, 2);
  assert.equal(f.sent.filter(batch => batch.bookmarkObservation?.kind === "snapshot").length, 2);
  assert.equal(f.sent.flatMap(batch => batch.downloads ?? []).filter(event => event.kind === "snapshot").length, 2);
  await flushCoverage();
});

test("database reset replays pending identities before the new baseline", async () => {
  const f = browser(); f.items = [downloadsItem];
  const tabs = observeActiveTabs(), bookmarks = observeBookmarks(), downloads = observeDownloads();
  await tabs.snapshot("one"); await bookmarks.sync("one"); await downloads.sync("one");
  const sender = chrome.runtime.sendNativeMessage;
  chrome.runtime.sendNativeMessage = async () => { throw new Error("receiver offline"); };
  f.tab.title = "B"; f.nodes[0].title = "B"; f.items[0] = { ...downloadsItem, state: "complete" };
  f.emit("updated", 1, { title: "B" }, f.tab);
  f.emit("onChanged", "1", { title: "B" });
  f.emit("downloadChanged", { id: 8, state: { current: "complete" } });
  await settle();
  await assert.rejects(tabs.flush(), /receiver offline/);
  const ids = [f.local.activeTabFacts.pending[0].eventId, f.local.bookmarkCaptureState.pending[0].observationId, f.local.downloadFacts.pending[0].eventId];
  chrome.runtime.sendNativeMessage = sender;
  const start = f.sent.length;
  await tabs.sync("two"); await bookmarks.sync("two"); await downloads.sync("two", false);
  const replay = f.sent.slice(start);
  assert.equal(replay.flatMap(batch => batch.activeTabEvents ?? [])[0].eventId, ids[0]);
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
