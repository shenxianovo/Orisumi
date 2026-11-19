import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";
import { coverage } from "./coverage.js";

type EventKind = "snapshot" | "window_created" | "window_removed" | "window_focus_changed" | "tab_created" |
  "tab_removed" | "tab_updated" | "tab_activated" | "tab_detached" | "tab_attached" | "tab_replaced";
type Tab = { windowId: number | null; url: string | null; title: string | null };
type Inventory = {
  tabs: Partial<Record<number, Tab>>;
  windows: Partial<Record<number, { activeTabId: number | null }>>;
  focusedWindowId: number | null;
};
type Changes = {
  windows?: { windowId: number; isPresent: boolean }[];
  tabs?: { tabId: number; fields: Partial<Tab> & { isPresent?: boolean } }[];
  focus?: { tabId: number | null };
};
export type TabEvent = Changes & {
  eventId: string; captureSessionId: string; sessionSequence: number; runStartedAtUtcMs: number;
  observedAtUtcMs: number; eventKind: EventKind;
};
type State = {
  browserSession?: string; captureSessionId?: string; sessionSequence: number; runStartedAtUtcMs?: number;
  pending: TabEvent[]; databaseId?: string; needsBaseline?: boolean; checkedAt?: number; inventory: Inventory;
};
type Update = {
  at: number; kind: EventKind;
  apply: (inventory: Inventory) => Changes | void | Promise<Changes | void>;
};

function emptyInventory(): Inventory {
  return { tabs: {}, windows: {}, focusedWindowId: null };
}
function focusedTab(state: Inventory) {
  if (state.focusedWindowId === null) return null;
  const tabId = state.windows[state.focusedWindowId]?.activeTabId;
  return tabId != null && state.tabs[tabId]?.windowId === state.focusedWindowId ? tabId : null;
}
function addWindow(state: Inventory, windowId: number, changes: Changes) {
  if (state.windows[windowId]) return;
  state.windows[windowId] = { activeTabId: null };
  changes.windows = [{ windowId, isPresent: true }];
}
function addTab(state: Inventory, tab: Pick<chrome.tabs.Tab, "active" | "windowId" | "url" | "title">, tabId: number, changes: Changes) {
  if (tab.active) state.windows[tab.windowId]!.activeTabId = tabId;
  if (state.tabs[tabId]) return;
  const fields = { windowId: tab.windowId, url: tab.url ?? null, title: tab.title ?? null };
  state.tabs[tabId] = fields;
  (changes.tabs ??= []).push({ tabId, fields: { ...fields, isPresent: true } });
}
function deselect(state: Inventory, tabId: number, windowId: number | null) {
  if (windowId === null) return;
  const window = state.windows[windowId];
  if (window?.activeTabId === tabId) window.activeTabId = null;
}
function removeTab(state: Inventory, tabId: number, changes: Changes) {
  const tab = state.tabs[tabId];
  if (!tab) return;
  deselect(state, tabId, tab.windowId);
  delete state.tabs[tabId];
  (changes.tabs ??= []).push({ tabId, fields: { isPresent: false } });
}
// Compare fields, not JSON: chrome.storage returns object keys sorted rather than in insertion order.
function sameInventory(a: Inventory, b: Inventory) {
  const tabs = Object.keys(a.tabs).map(Number), windows = Object.keys(a.windows).map(Number);
  return a.focusedWindowId === b.focusedWindowId &&
    tabs.length === Object.keys(b.tabs).length && windows.length === Object.keys(b.windows).length &&
    tabs.every(id => {
      const x = a.tabs[id]!, y = b.tabs[id];
      return y?.windowId === x.windowId && y.url === x.url && y.title === x.title;
    }) &&
    windows.every(id => b.windows[id]?.activeTabId === a.windows[id]!.activeTabId);
}
function baseline(state: Inventory): Changes {
  return {
    windows: Object.keys(state.windows).map(id => ({ windowId: Number(id), isPresent: true })),
    tabs: Object.entries(state.tabs).map(([id, tab]) => ({ tabId: Number(id), fields: { ...tab, isPresent: true } })),
    focus: { tabId: focusedTab(state) },
  };
}

const windowFilter = { windowTypes: ["normal", "popup", "panel", "app", "devtools"] } satisfies chrome.windows.QueryOptions;

export function observeTabs() {
  let operations: Promise<unknown> = Promise.resolve();
  let flushing: Promise<void> | undefined;
  let snapshotting: Promise<void> | undefined;
  let buffered: Update[] | undefined = [];
  let needsBaseline = false;
  const started = Date.now();

  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = operations.then(operation);
    operations = result.catch(() => {});
    return result;
  }
  async function readState(): Promise<State> {
    return (await chrome.storage.local.get<{ tabFacts?: State }>("tabFacts")).tabFacts ??
      { sessionSequence: 0, pending: [], inventory: emptyInventory() };
  }
  function save(state: State) { return chrome.storage.local.set({ tabFacts: state }); }
  async function browserSession() {
    let { tabBrowserSession } = await chrome.storage.session.get<{ tabBrowserSession?: string }>("tabBrowserSession");
    if (!tabBrowserSession) {
      tabBrowserSession = crypto.randomUUID();
      await chrome.storage.session.set({ tabBrowserSession });
    }
    return tabBrowserSession;
  }
  function append(state: State, eventKind: EventKind, at: number, changes: Changes) {
    state.pending.push({ ...changes, eventKind, eventId: crypto.randomUUID(), captureSessionId: state.captureSessionId!,
      sessionSequence: ++state.sessionSequence, runStartedAtUtcMs: state.runStartedAtUtcMs!, observedAtUtcMs: at });
  }
  async function markGap(state: State, detail: string) {
    await coverage("tabs", "unavailable", detail, "notification", state.checkedAt ?? started, null, state.captureSessionId);
  }
  async function fail(state: State, detail: string) {
    needsBaseline = true;
    state.needsBaseline = true;
    await save(state);
    await markGap(state, detail);
  }
  async function apply(state: State, update: Update) {
    const previous = focusedTab(state.inventory);
    const changes = await update.apply(state.inventory) ?? {};
    const tabId = focusedTab(state.inventory);
    if (previous !== tabId) changes.focus = { tabId };
    if (changes.windows || changes.tabs || changes.focus) append(state, update.kind, update.at, changes);
  }
  function dispatch(update: Update) {
    void serialize(async () => {
      const state = await readState();
      if (needsBaseline || state.needsBaseline) return;
      try {
        await apply(state, update);
        await save(state);
      } catch (error) {
        // Reload so an unsuccessful update cannot advance the durable state.
        needsBaseline = true;
        await fail(await readState(), "event_failed");
        throw error;
      }
    }).catch(error => console.error("Could not record browser tabs", error));
  }
  function record(kind: EventKind, apply: Update["apply"]) {
    const update = { at: Date.now(), kind, apply };
    if (buffered) buffered.push(update);
    else if (!needsBaseline) dispatch(update);
  }

  chrome.windows.onCreated.addListener(window => {
    if (window.incognito || window.id == null || window.id < 0) return;
    const { id, focused } = window;
    record("window_created", state => {
      const changes: Changes = {};
      addWindow(state, id, changes);
      if (focused) state.focusedWindowId = id;
      return changes;
    });
  }, windowFilter);
  chrome.windows.onRemoved.addListener(windowId => record("window_removed", state => {
    if (!state.windows[windowId]) return;
    const changes: Changes = { windows: [{ windowId, isPresent: false }] };
    for (const [id, tab] of Object.entries(state.tabs)) {
      if (tab?.windowId === windowId) removeTab(state, Number(id), changes);
    }
    if (state.focusedWindowId === windowId) state.focusedWindowId = null;
    delete state.windows[windowId];
    return changes;
  }), windowFilter);
  chrome.windows.onFocusChanged.addListener(windowId => record("window_focus_changed", state => {
    state.focusedWindowId = state.windows[windowId] ? windowId : null;
  }), windowFilter);
  chrome.tabs.onActivated.addListener(({ tabId, windowId }) => record("tab_activated", state => {
    const window = state.windows[windowId];
    if (window) window.activeTabId = tabId;
  }));
  chrome.tabs.onCreated.addListener(tab => {
    if (tab.incognito || tab.id == null || tab.id < 0 || tab.windowId < 0) return;
    const id = tab.id, captured = { ...tab };
    record("tab_created", state => {
      const changes: Changes = {};
      addWindow(state, captured.windowId, changes);
      addTab(state, captured, id, changes);
      return changes;
    });
  });
  chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
    if (tab.incognito || (change.url === undefined && change.title === undefined)) return;
    const { url, title } = change;
    record("tab_updated", state => {
      const current = state.tabs[tabId];
      if (!current) return;
      const fields: Partial<Tab> = {};
      if (url !== undefined && url !== current.url) fields.url = current.url = url;
      if (title !== undefined && title !== current.title) fields.title = current.title = title;
      if (Object.keys(fields).length) return { tabs: [{ tabId, fields }] };
    });
  });
  chrome.tabs.onRemoved.addListener(tabId => record("tab_removed", state => {
    const changes: Changes = {};
    removeTab(state, tabId, changes);
    return changes;
  }));
  chrome.tabs.onDetached.addListener((tabId, { oldWindowId }) => record("tab_detached", state => {
    const tab = state.tabs[tabId];
    if (tab?.windowId !== oldWindowId) return;
    deselect(state, tabId, oldWindowId);
    tab.windowId = null;
    return { tabs: [{ tabId, fields: { windowId: null } }] };
  }));
  chrome.tabs.onAttached.addListener((tabId, { newWindowId }) => record("tab_attached", state => {
    const tab = state.tabs[tabId];
    if (!tab || tab.windowId === newWindowId) return;
    const changes: Changes = { tabs: [{ tabId, fields: { windowId: newWindowId } }] };
    addWindow(state, newWindowId, changes);
    deselect(state, tabId, tab.windowId);
    tab.windowId = newWindowId;
    return changes;
  }));
  chrome.tabs.onReplaced.addListener((added, removed) => {
    // The callback gives the two IDs but omits the new tab's fields.
    const read = chrome.tabs.get(added).then(tab => ({ tab }), error => ({ error }));
    record("tab_replaced", async state => {
      const changes: Changes = {};
      const oldWindowId = state.tabs[removed]?.windowId;
      const wasSelected = oldWindowId != null && state.windows[oldWindowId]?.activeTabId === removed;
      removeTab(state, removed, changes);
      const result = await read;
      if ("tab" in result && result.tab.incognito) return changes;

      // Replacement occurs in the old tab's strip. The read may finish after the new tab moves or closes.
      if (oldWindowId != null) {
        const tab = "tab" in result ? result.tab : undefined;
        addTab(state, { windowId: oldWindowId, active: wasSelected, url: tab?.url, title: tab?.title }, added, changes);
      } else if ("error" in result) {
        throw result.error;
      } else if (result.tab.windowId >= 0) {
        addWindow(state, result.tab.windowId, changes);
        addTab(state, result.tab, added, changes);
      }
      return changes;
    });
  });

  async function readInventory() {
    const [tabs, windows] = await Promise.all([chrome.tabs.query({}), chrome.windows.getAll(windowFilter)]);
    const state = emptyInventory();
    for (const window of windows) {
      if (window.incognito || window.id == null || window.id < 0) continue;
      state.windows[window.id] = { activeTabId: null };
      if (window.focused) state.focusedWindowId = window.id;
    }
    for (const tab of tabs) {
      if (tab.incognito || tab.id == null || tab.id < 0 || !state.windows[tab.windowId]) continue;
      state.tabs[tab.id] = { windowId: tab.windowId, url: tab.url ?? null, title: tab.title ?? null };
      if (tab.active) state.windows[tab.windowId]!.activeTabId = tab.id;
    }
    return state;
  }
  async function snapshot(databaseId?: string) {
    while (snapshotting) await snapshotting.catch(() => {});
    buffered ??= [];
    snapshotting = serialize(async () => {
      const queue = buffered ??= [];
      const state = await readState();
      const session = await browserSession();
      const databaseChanged = !!(databaseId && state.databaseId && state.databaseId !== databaseId);
      let reason = databaseChanged ? "database_replaced" :
        state.browserSession !== session || needsBaseline || state.needsBaseline ? "baseline_reset" : null;
      let accepted: State | undefined;
      try {
        for (let attempt = 0; attempt < 3; attempt++) {
          const cut = queue.length;
          const inventory = await readInventory();
          const at = Date.now();
          if (cut !== queue.length) continue;
          const candidate = { ...state, inventory: structuredClone(state.inventory), pending: [] as TabEvent[] };
          if (!reason) {
            // Recover waking notifications in order before comparing with the live inventory.
            for (const update of queue.slice(0, cut)) await apply(candidate, update);
            if (!sameInventory(candidate.inventory, inventory)) reason = "reconciliation_changed";
          }
          if (reason) {
            if (state.captureSessionId) await markGap(state, reason);
            if (cut && !state.captureSessionId) await markGap(state, "initial_snapshot_raced");
            candidate.pending = [];
            candidate.captureSessionId = crypto.randomUUID();
            candidate.runStartedAtUtcMs = at;
            candidate.sessionSequence = 0;
            candidate.inventory = inventory;
            append(candidate, "snapshot", at, baseline(inventory));
          }
          candidate.pending = [...state.pending, ...candidate.pending];
          candidate.browserSession = session;
          candidate.databaseId = databaseId ?? state.databaseId;
          candidate.checkedAt = at;
          candidate.needsBaseline = false;
          await save(candidate);
          accepted = candidate;
          needsBaseline = false;
          buffered = undefined;
          for (const update of queue.slice(cut)) dispatch(update);
          break;
        }
      } catch (error) {
        await fail(state, "snapshot_failed");
        throw error;
      }
      if (accepted) {
        await coverage("tabs", "available", "baseline_received", "notification", accepted.runStartedAtUtcMs!, null, accepted.captureSessionId);
      } else {
        await fail(state, "snapshot_raced");
      }
    }).finally(() => { snapshotting = undefined; });
    return snapshotting;
  }

  async function drain() {
    while (true) {
      const { pending } = await serialize(readState);
      if (!pending.length) return;
      let count = 0, bytes = 0;
      while (count < pending.length) {
        const size = jsonBytes(pending[count]) + 1;
        if (bytes + size > batchBytesAvailable) break;
        bytes += size;
        count++;
      }
      if (!count) throw new Error("A tab observation exceeds the local collector message limit.");
      await sendBatch({ tabEvents: pending.slice(0, count) });
      await serialize(async () => {
        const state = await readState();
        state.pending.splice(0, count);
        await save(state);
      });
    }
  }
  function flush(): Promise<void> { return flushing ??= drain().finally(() => { flushing = undefined; }); }
  return { snapshot, flush, async sync(databaseId: string) { await snapshot(databaseId); await flush(); } };
}
