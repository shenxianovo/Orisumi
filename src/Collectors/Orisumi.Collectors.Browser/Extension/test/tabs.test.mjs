import assert from "node:assert/strict";
import test from "node:test";
import { observeTabs } from "../../ChromeExtension/tabs.js";
import { flushCoverage } from "../../ChromeExtension/coverage.js";

function browser() {
  const local = {}, session = {}, listeners = {}, sent = [];
  const storage = data => ({ get: async keys => Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, structuredClone(data[key])])),
    set: async values => Object.assign(data, structuredClone(values)) });
  const event = name => ({ addListener: callback => { listeners[name] = callback; } });
  const tab = (id, windowId, details = {}) => ({ id, windowId, active: false, incognito: false, url: "A", title: "A", ...details });
  const f = { local, session, sent, tab, emit: (name, ...args) => listeners[name](...args),
    tabs: [tab(1, 2, { active: true }), tab(3, 2), tab(4, 5, { active: true }), tab(9, 10, { active: true, incognito: true })],
    windows: [{ id: 2, focused: true }, { id: 5, focused: false }, { id: 10, focused: false, incognito: true }] };
  globalThis.chrome = {
    storage: { local: storage(local), session: storage(session) },
    tabs: { onActivated: event("activated"), onUpdated: event("updated"), onCreated: event("tabCreated"), onRemoved: event("tabRemoved"),
      onAttached: event("attached"), onDetached: event("detached"), onReplaced: event("replaced"),
      get: async id => structuredClone(f.tabs.find(tab => tab.id === id)), query: async () => structuredClone(f.tabs) },
    windows: { onFocusChanged: event("focus"), onRemoved: event("windowRemoved"), onCreated: event("windowCreated"),
      getAll: async () => structuredClone(f.windows) },
    runtime: { sendNativeMessage: async (_host, request) => { sent.push(structuredClone(request)); return { ok: true, inserted: 1 }; } },
  };
  f.facts = () => sent.flatMap(batch => batch.tabEvents ?? []);
  return f;
}

test("all tabs have an initial value, URL A→B→A and every title change; unchanged values and private entities produce no facts", async () => {
  const f = browser(), collector = observeTabs();
  await collector.sync("one");
  const [initial] = f.facts();
  assert.deepEqual(initial.tabs.map(tab => tab.tabId), [1, 3, 4]);
  assert.deepEqual(initial.windows.map(window => window.windowId), [2, 5]);
  assert.deepEqual(initial.focus, { tabId: 1 });
  const background = f.tabs[1];
  f.emit("updated", 3, { url: "B" }, background);
  f.emit("updated", 3, { url: "A", title: "Loading" }, background);
  f.emit("updated", 3, { title: "" }, background);
  f.emit("updated", 3, { title: "" }, background);
  background.title = "";
  f.emit("tabCreated", f.tab(11, 10, { incognito: true }));
  f.emit("updated", 9, { title: "Private" }, f.tabs[3]);
  await collector.sync("one");
  assert.deepEqual(f.facts().slice(1).map(event => event.tabs), [
    [{ tabId: 3, fields: { url: "B" } }],
    [{ tabId: 3, fields: { url: "A", title: "Loading" } }],
    [{ tabId: 3, fields: { title: "" } }],
  ]);
  f.emit("focus", 10);
  await collector.flush();
  assert.deepEqual(f.facts().at(-1).focus, { tabId: null });
});

test("create, detach, attach, replace and close keep tab and window lifetimes and foreground focus", async () => {
  const f = browser(), collector = observeTabs();
  await collector.sync("one");
  f.emit("windowCreated", { id: 6, focused: false });
  f.emit("tabCreated", f.tab(7, 6, { url: undefined, title: undefined, active: true }));
  f.emit("detached", 7, { oldWindowId: 6 });
  f.emit("attached", 7, { newWindowId: 2 });
  f.emit("activated", { tabId: 7, windowId: 2 });
  f.tabs.push(f.tab(8, 2, { url: "Replacement", title: "", active: true }));
  f.emit("replaced", 8, 7);
  f.emit("tabRemoved", 7);
  f.emit("tabRemoved", 8);
  f.emit("windowRemoved", 6);
  f.emit("windowRemoved", 5);
  f.emit("tabRemoved", 4);
  await collector.flush();
  const events = f.facts().slice(1);
  assert.deepEqual(events.map(event => event.eventKind), ["window_created", "tab_created", "tab_detached", "tab_attached",
    "tab_activated", "tab_replaced", "tab_removed", "window_removed", "window_removed"]);
  assert.deepEqual(events[1].tabs[0].fields, { isPresent: true, windowId: 6, url: null, title: null });
  assert.deepEqual(events.slice(2, 4).map(event => event.tabs[0].fields.windowId), [null, 2]);
  assert.deepEqual(events.slice(4, 7).map(event => event.focus), [{ tabId: 7 }, { tabId: 8 }, { tabId: null }]);
  assert.deepEqual(events[5].tabs, [{ tabId: 7, fields: { isPresent: false } },
    { tabId: 8, fields: { isPresent: true, windowId: 2, url: "Replacement", title: "" } }]);
  assert.deepEqual(events.at(-1).tabs, [{ tabId: 4, fields: { isPresent: false } }]);
});

test("a replacement can close before its initial fields are readable", async () => {
  const f = browser(), collector = observeTabs();
  await collector.sync("one");
  chrome.tabs.get = async () => { throw new Error("replacement already closed"); };
  f.emit("replaced", 8, 1);
  f.emit("tabRemoved", 8);
  await collector.flush();
  assert.deepEqual(f.facts().slice(1).map(event => event.tabs), [
    [{ tabId: 1, fields: { isPresent: false } }, { tabId: 8, fields: { isPresent: true, windowId: 2, url: null, title: null } }],
    [{ tabId: 8, fields: { isPresent: false } }],
  ]);
});

test("initial read rejects a raced snapshot and keeps callbacks arriving after the accepted read", async () => {
  const f = browser(), collector = observeTabs();
  const read = chrome.tabs.query, write = chrome.storage.local.set;
  let reads = 0;
  chrome.tabs.query = async () => {
    const stale = await read();
    if (++reads === 1) {
      f.tabs[1].title = "B";
      f.emit("updated", 3, { title: "B" }, f.tabs[1]);
    }
    return stale;
  };
  chrome.storage.local.set = async values => {
    if (!f.local.tabFacts && values.tabFacts?.pending.length) {
      f.tabs[1].title = "C";
      f.emit("updated", 3, { title: "C" }, f.tabs[1]);
    }
    await write(values);
  };
  await collector.sync("one");
  await flushCoverage();
  assert.equal(reads, 2);
  assert.deepEqual(f.facts().map(event => event.tabs.find(tab => tab.tabId === 3).fields.title), ["B", "C"]);
  assert.ok(f.sent.flatMap(batch => batch.coverage ?? []).some(event => event.detail === "initial_snapshot_raced"));
});

test("worker wake replays buffered notifications without a new baseline; missed changes and a new browser session reset it", async () => {
  const f = browser();
  await observeTabs().sync("one");
  const wake = observeTabs();
  f.emit("updated", 3, { url: "B" }, f.tabs[1]);
  f.emit("updated", 3, { url: "A" }, f.tabs[1]);
  await wake.sync("one");
  const previous = f.local.tabFacts.captureSessionId;
  assert.deepEqual(f.facts().map(event => event.sessionSequence), [1, 2, 3]);
  assert.ok(f.facts().every(event => event.captureSessionId === previous));
  f.tabs[1].url = "C";
  await observeTabs().sync("one");
  assert.notEqual(f.local.tabFacts.captureSessionId, previous);
  const reset = f.local.tabFacts.captureSessionId;
  delete f.session.tabBrowserSession;
  await observeTabs().sync("one");
  await flushCoverage();
  assert.notEqual(f.local.tabFacts.captureSessionId, reset);
  assert.equal(f.facts().filter(event => event.eventKind === "snapshot").length, 3);
  assert.ok(f.sent.flatMap(batch => batch.coverage ?? []).some(event => event.runId === previous && event.evidence === "unavailable"));
});

test("a snapshot read failure durably requires a new baseline, including unchanged values", async () => {
  const f = browser();
  const collector = observeTabs();
  await collector.sync("one");
  const oldRun = f.local.tabFacts.captureSessionId;
  const query = chrome.tabs.query;
  chrome.tabs.query = async () => { throw new Error("tabs unavailable"); };
  await assert.rejects(collector.snapshot(), /tabs unavailable/);
  assert.equal(f.local.tabFacts.needsBaseline, true);
  chrome.tabs.query = query;
  await observeTabs().sync("one");
  await flushCoverage();
  assert.equal(f.facts().length, 2);
  assert.equal(f.facts().at(-1).sessionSequence, 1);
  assert.notEqual(f.local.tabFacts.captureSessionId, oldRun);
});

test("an interrupted delivery reuses the durable event ID after worker wake", async () => {
  const f = browser(), tabs = observeTabs();
  await tabs.sync("one");
  f.tabs[1].title = "New title";
  f.emit("updated", 3, { title: "New title" }, f.tabs[1]);
  const send = chrome.runtime.sendNativeMessage;
  chrome.runtime.sendNativeMessage = async (...args) => {
    await send(...args);
    throw new Error("ack lost");
  };
  await assert.rejects(tabs.flush(), /ack lost/);
  const unsent = f.local.tabFacts.pending[0];
  chrome.runtime.sendNativeMessage = send;
  await observeTabs().sync("one");
  assert.deepEqual(f.facts().slice(1), [unsent, unsent]);
  assert.equal(f.local.tabFacts.pending.length, 0);
});
