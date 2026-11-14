import assert from "node:assert/strict";
import test from "node:test";
import { observeActiveTabs } from "../../ChromeExtension/active-tabs.js";

test("title-only callbacks are collected and private focus closes regular-tab observation", async () => {
  const local = {}, session = {}, listeners = {}, sent = [];
  const storage = data => ({ get: async keys => Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, structuredClone(data[key])])),
    set: async values => Object.assign(data, structuredClone(values)) });
  const event = name => ({ addListener: callback => { listeners[name] = callback; } });
  let tab = { id: 1, windowId: 2, active: true, incognito: false, url: "https://example.com", title: "A" };
  const window = () => ({ id: 2, focused: true, tabs: [tab] });
  globalThis.chrome = { storage: { local: storage(local), session: storage(session) },
    tabs: { onActivated: event("activated"), onUpdated: event("updated"), onRemoved: event("tabRemoved"), get: async () => tab },
    windows: { WINDOW_ID_NONE: -1, onFocusChanged: event("focus"), onRemoved: event("windowRemoved"), get: async () => window(), getAll: async () => [window()] },
    runtime: { sendNativeMessage: async (_host, batch) => { sent.push(...batch.activeTabEvents); return { ok: true, inserted: 1 }; } } };
  const collector = observeActiveTabs();
  await collector.snapshot(); await collector.flush();
  tab = { ...tab, title: "B" };
  listeners.updated(1, { title: "B" }, tab); await collector.flush();
  assert.deepEqual(sent.map(event => event.title), ["A", "B"]);
  tab = { ...tab, incognito: true };
  listeners.focus(2); await collector.flush();
  assert.equal(sent.at(-1).tabId, null);
  assert.equal(sent.at(-1).url, null);
});
