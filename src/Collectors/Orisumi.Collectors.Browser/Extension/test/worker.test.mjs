import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { setTimeout as sleep } from "node:timers/promises";
import test from "node:test";
import { IDBFactory } from "fake-indexeddb";
import { openOutbox } from "../bin/test-extension/outbox.js";

function event() {
  const listeners = [];
  return { addListener: listener => listeners.push(listener), emit: (...args) => listeners.forEach(listener => listener(...args)),
    get count() { return listeners.length; } };
}
function storage(saved = {}) {
  return {
    saved,
    get: async name => ({ [name]: structuredClone(saved[name]) }),
    set: async values => Object.assign(saved, structuredClone(values)),
  };
}
const window = (id, incognito, tabs, focused = false) => ({ id, incognito, focused, tabs });
const tab = (id, windowId, incognito, title, active = true) => ({
  id, windowId, incognito, title, active, url: incognito ? "https://private.test" : "https://example.test",
});
let workerNumber = 0;
async function eventually(check) {
  for (let i = 0; i < 200; i++) {
    const value = await check();
    if (value) return value;
    await sleep(10);
  }
  assert.fail("extension did not reach the expected state");
}

async function harness(t) {
  t.mock.method(console, "error", () => {});
  globalThis.indexedDB = new IDBFactory();
  const retries = [];
  t.mock.method(globalThis, "setTimeout", (callback, delay) => retries.push({ callback, delay }));

  const normalTab = tab(7, 1, false, "A");
  const privateTab = tab(55, 99, true, "private title");
  const windows = [window(1, false, [normalTab], true), window(99, true, [privateTab])];
  const items = [{ id: 3, incognito: false, filename: "/tmp/A", state: "in_progress" },
    { id: 44, incognito: true, filename: "/tmp/private" }];
  const base = {
    storage: {
      local: storage(),
      session: storage(),
    },
    alarms: { onAlarm: event(), get: async () => undefined, create: async () => {} },
    tabs: { onCreated: event(), onUpdated: event(), onRemoved: event(), onActivated: event(), onDetached: event(),
      onAttached: event(), onReplaced: event(), get: async id => windows.flatMap(window => window.tabs).find(tab => tab.id === id) },
    windows: { WINDOW_ID_NONE: -1, onCreated: event(), onRemoved: event(), onFocusChanged: event(),
      getAll: async options => { assert.equal(options.populate, true); return structuredClone(windows); },
      get: async id => windows.find(window => window.id === id) },
    downloads: { onCreated: event(), onChanged: event(), onErased: event(),
      search: async query => query.id === undefined ? structuredClone(items) : items.filter(item => item.id === query.id) },
    bookmarks: { onCreated: event(), onChanged: event(), onRemoved: event(), onMoved: event(), onChildrenReordered: event(),
      getTree: async () => [{ id: "0", title: "", children: [{ id: "1", parentId: "0", title: "A", index: 0 }] }] },
    history: { onVisitRemoved: event(), search: async () => [], getVisits: async () => [] },
  };
  const harness = { ...base, ports: [], retries, controls: { hold: () => false, history: undefined, acceptOpen: true } };
  t.mock.method(globalThis, "fetch", async url => {
    assert.equal(url, "chrome-extension://fixture/config.json");
    return new Response(JSON.stringify({ instance: "dev_fixture", port: 47971 }));
  });
  class MockWebSocket extends EventTarget {
    static CLOSING = 2;
    readyState = 0;
    messages = [];
    constructor(url) {
      super();
      assert.equal(url, "ws://127.0.0.1:47971/");
      harness.ports.push(this);
      queueMicrotask(() => {
        this.readyState = 1;
        this.dispatchEvent(new Event("open"));
      });
    }
    close() {
      if (this.readyState >= MockWebSocket.CLOSING) return;
      this.readyState = 3;
      this.dispatchEvent(new Event("close"));
    }
    reply(value) { this.dispatchEvent(new MessageEvent("message", { data: JSON.stringify(value) })); }
    ack(sequence) { this.reply(sequence === undefined ? { ok: true } : { ok: true, sequence }); }
    send(value) {
      assert.equal(this.readyState, 1);
      const request = JSON.parse(value);
      this.messages.push(request);
      if (request.type === "open") {
        if (harness.controls.acceptOpen) queueMicrotask(() => this.ack());
      } else if (request.type === "history_poll") {
        const history = harness.controls.history;
        harness.controls.history = undefined;
        queueMicrotask(() => this.reply({ ok: true, history }));
      } else if (!harness.controls.hold(request)) queueMicrotask(() => this.ack(request.sequence));
    }
  }
  const websocket = globalThis.WebSocket;
  globalThis.WebSocket = MockWebSocket;
  t.after(() => { globalThis.WebSocket = websocket; });
  globalThis.chrome = { ...base, runtime: { getURL: path => `chrome-extension://fixture/${path}`, onInstalled: event(), onStartup: event() } };
  harness.worker = () => import(`../bin/test-extension/worker.js?worker=${++workerNumber}`);
  harness.ready = async () => {
    await harness.worker();
    const port = await eventually(() => {
      const port = harness.ports[0];
      return port?.messages.some(m => m.type === "history_poll") && deliveries(port).length >= 3 && port;
    });
    harness.outbox = await openOutbox();
    return port;
  };
  return harness;
}
const deliveries = port => port.messages.filter(message => message.type === "delivery");

test("retryable delivery replies back off without dropping or skipping the queue head", async t => {
  const h = await harness(t);
  const first = await h.ready();
  h.controls.hold = message => message.kind === "tab_updated";
  h.tabs.onUpdated.emit(7, { title: "B" }, tab(7, 1, false, "B"));
  h.tabs.onUpdated.emit(7, { title: "C" }, tab(7, 1, false, "C"));
  const pending = await eventually(() => deliveries(first).find(message => message.kind === "tab_updated"));
  first.reply({ ok: false, error: "busy" });
  await eventually(() => h.retries[0]);
  assert.equal(h.retries[0].delay, 1000);
  h.retries[0].callback();
  const replay = await eventually(() => h.ports[1]?.messages.find(message => message.type === "delivery"));
  assert.deepEqual(replay, pending);
  h.ports[1].reply({ ok: false, error: "busy" });
  await eventually(() => h.retries[1]);
  assert.equal(h.retries[1].delay, 2000);
  assert.equal((await h.outbox.first()).sequence, pending.sequence);
  h.retries[1].callback();
  await eventually(() => deliveries(h.ports[2] ?? { messages: [] }).length);
  h.ports[2].reply({ ok: true, rejected: true, sequence: pending.sequence });
  const following = await eventually(() => deliveries(h.ports[2])[1]);
  assert.equal(following.sequence, pending.sequence + 1);
  assert.equal(following.data.change.title, "C");
});

test("startup creates installation identity and forwards full snapshots and absolute events", async t => {
  const h = await harness(t);
  const port = await h.ready();
  const { profileId } = port.messages[0];
  assert.match(profileId, /^[0-9a-f-]{36}$/);
  assert.deepEqual(h.storage.local.saved, { profileId });
  assert.deepEqual(h.storage.session.saved, { browserSession: port.messages[0].browserSession });
  assert.deepEqual(deliveries(port).map(message => message.scope).sort(), ["bookmarks", "downloads", "tabs"]);
  for (const delivery of deliveries(port)) assert.equal(delivery.kind, "snapshot");
  const bookmarkSnapshot = deliveries(port).find(message => message.scope === "bookmarks");
  assert.deepEqual(bookmarkSnapshot.data, [{ id: "0", title: "" }, { id: "1", parentId: "0", title: "A" }]);

  h.tabs.onUpdated.emit(7, { title: "B" }, tab(7, 1, false, "B"));
  h.tabs.onUpdated.emit(7, { title: "A" }, tab(7, 1, false, "A"));
  h.bookmarks.onMoved.emit("1", { parentId: "2", oldParentId: "0", index: 4, oldIndex: 0 });
  h.downloads.onChanged.emit({ id: 3, filename: { previous: "/tmp/A", current: "/tmp/B" } });
  await eventually(() => deliveries(port).length === 7);
  const events = deliveries(port).slice(3);
  assert.deepEqual(events.map(message => message.data), [
    { tabId: 7, change: { title: "B" } }, { tabId: 7, change: { title: "A" } }, { id: "1", parentId: "2" },
    { id: 3, filename: { previous: "/tmp/A", current: "/tmp/B" } },
  ]);
  assert.deepEqual(events.map(message => message.sequence), [4, 5, 6, 7]);
  assert.equal(h.bookmarks.onChildrenReordered.count, 0);
  h.alarms.onAlarm.emit({ name: "orisumi-capture" });
  await eventually(() => deliveries(port).length === 8);
  assert.equal(deliveries(port)[7].kind, "snapshot");
  assert.equal(deliveries(port)[7].scope, "tabs");
});

test("outbox survives a disconnected WebSocket, backs off and replays the exact pending sequence until ACK", async t => {
  const h = await harness(t);
  const first = await h.ready();
  h.controls.hold = message => message.kind === "tab_updated";
  h.tabs.onUpdated.emit(7, { title: "B" }, tab(7, 1, false, "B"));
  const pending = await eventually(() => deliveries(first).find(message => message.kind === "tab_updated"));
  assert.equal((await h.outbox.first()).sequence, pending.sequence);
  first.close();
  await eventually(() => h.retries.length === 1);
  assert.equal(h.retries[0].delay, 1000);

  h.tabs.onUpdated.emit(7, { title: "A" }, tab(7, 1, false, "A"));
  h.controls.acceptOpen = false;
  h.retries[0].callback();
  const second = await eventually(() => h.ports[1]?.messages.length && h.ports[1]);
  second.close();
  await eventually(() => h.retries.length === 2);
  assert.equal(h.retries[1].delay, 2000);
  assert.equal((await (await openOutbox()).first()).sequence, pending.sequence);

  h.controls.acceptOpen = true;
  h.retries[1].callback();
  const third = await eventually(() => deliveries(h.ports[2] ?? { messages: [] }).length && h.ports[2]);
  assert.deepEqual(deliveries(third)[0], pending);
  assert.equal(third.messages[0].queueId, first.messages[0].queueId);
  assert.equal(third.messages[0].profileId, first.messages[0].profileId);
  third.ack(pending.sequence);
  await eventually(() => deliveries(third).length >= 2);
  const next = deliveries(third)[1];
  assert.equal(next.sequence, pending.sequence + 1);
  assert.equal(next.data.change.title, "A");
  assert.equal((await h.outbox.first()).sequence, next.sequence);
  third.ack(next.sequence);
  await eventually(async () => !await h.outbox.first());
  third.close();
  await eventually(() => h.retries.length === 3);
  assert.equal(h.retries[2].delay, 1000);
});

test("incognito data is filtered before entering the queue, including focus and ID-only events", async t => {
  const h = await harness(t);
  const port = await h.ready();
  const privateTab = tab(55, 99, true, "private title");
  h.tabs.onCreated.emit(privateTab);
  h.tabs.onUpdated.emit(55, { title: "private change" }, privateTab);
  h.tabs.onActivated.emit({ tabId: 55, windowId: 99 });
  h.tabs.onAttached.emit(55, { newWindowId: 99, newPosition: 0 });
  h.tabs.onRemoved.emit(55, { windowId: 99 });
  h.windows.onCreated.emit(window(99, true, [privateTab]));
  h.windows.onFocusChanged.emit(99);
  h.windows.onRemoved.emit(99);
  h.downloads.onCreated.emit({ id: 44, incognito: true, filename: "/tmp/private" });
  h.downloads.onChanged.emit({ id: 44, filename: { current: "/tmp/private-change" } });
  h.downloads.onErased.emit(44);
  h.tabs.onUpdated.emit(7, { title: "ordinary change" }, tab(7, 1, false, "ordinary change"));
  await eventually(() => deliveries(port).some(message => message.kind === "tab_updated"));
  assert.deepEqual(deliveries(port).slice(3).map(message => [message.kind, message.data]), [
    ["window_focus_changed", { windowId: null }],
    ["tab_updated", { tabId: 7, change: { title: "ordinary change" } }],
  ]);
  const transported = JSON.stringify(deliveries(port).map(message => message.data));
  assert.doesNotMatch(transported, /private|\b(44|55|99)\b/);
  const manifest = JSON.parse(await readFile(new URL("../manifest.json", import.meta.url)));
  assert.equal(manifest.incognito, "not_allowed");
  assert.equal(manifest.minimum_chrome_version, "116");
  assert.ok(!manifest.permissions.includes("nativeMessaging") && !("host_permissions" in manifest));
});

test("a new worker replays the pending delivery with the same queue and Chrome session identity", async t => {
  const h = await harness(t);
  const oldWorker = await h.ready();
  h.controls.hold = message => message.kind === "tab_updated";
  h.tabs.onUpdated.emit(7, { title: "Still pending" }, tab(7, 1, false, "Still pending"));
  const pending = await eventually(() => deliveries(oldWorker).find(message => message.kind === "tab_updated"));
  oldWorker.close();
  await h.worker();
  const restarted = await eventually(() => h.ports[1] && deliveries(h.ports[1]).length && h.ports[1]);
  assert.deepEqual(restarted.messages[0], oldWorker.messages[0]);
  assert.deepEqual(deliveries(restarted)[0], pending);
  restarted.ack(pending.sequence);
  await eventually(async () => !await h.outbox.first());
});

test("history follows the receiver's range and collects retained visits and history titles", async t => {
  const h = await harness(t);
  const queries = [];
  const visit = { id: "13", visitId: "12", visitTime: 750.25, transition: "link", referringVisitId: "0", isLocal: true };
  h.history.search = async query => { queries.push(query); return [{ url: "https://example.test", title: "From history" }]; };
  h.history.getVisits = async ({ url }) => { assert.equal(url, "https://example.test"); return [visit]; };
  h.controls.history = { direction: "forward", cursor: 65000, startTime: 5000, endTime: 90000 };
  const port = await h.ready();
  const range = await eventually(() => deliveries(port).find(message => message.kind === "range"));
  assert.deepEqual(queries, [{ text: "", startTime: 5000, endTime: 90000, maxResults: 10000 }]);
  assert.deepEqual(range.data, { ...{ direction: "forward", cursor: 65000, startTime: 5000, endTime: 90000 },
    pages: [{ url: "https://example.test", title: "From history", visits: [visit] }] });
});

test("large deliveries use one WebSocket message and heartbeats leave the request pending", async t => {
  const h = await harness(t);
  const port = await h.ready();
  const title = ("🧪\\\n").repeat(300_000);
  h.controls.hold = message => message.kind === "tab_updated";
  h.tabs.onUpdated.emit(7, { title }, tab(7, 1, false, title));
  const message = await eventually(() => deliveries(port).find(message => message.kind === "tab_updated"));
  assert.equal(port.messages.filter(frame => frame.kind === "tab_updated").length, 1);
  assert.equal(message.data.change.title, title);
  port.reply({ type: "heartbeat" });
  await sleep(20);
  assert.equal(h.retries.length, 0);
  assert.equal((await h.outbox.first()).sequence, message.sequence);
  port.ack(message.sequence);
  await eventually(async () => !await h.outbox.first());
});
