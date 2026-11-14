import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";
import { coverage } from "./coverage.js";

type EventKind = "snapshot" | "window_blurred" | "window_focused" | "tab_activated" | "url_changed" | "title_changed" | "tab_removed" | "window_removed";
type Tab = Pick<chrome.tabs.Tab, "id" | "windowId" | "url" | "title">;
type TabLocation = { windowId: number | null; tabId: number | null; url: string | null };
export type ActiveTabEvent = TabLocation & {
  eventId: string; captureSessionId: string; sessionSequence: number; runStartedAtUtcMs: number;
  observedAtUtcMs: number; eventKind: EventKind; title: string | null;
};
type TabState = Partial<TabLocation> & {
  captureSessionId?: string; title?: string | null; sessionSequence: number;
  pending: ActiveTabEvent[]; databaseId?: string; baselineSession?: string; needsBaseline?: boolean;
};

export function observeActiveTabs() {
  let operations: Promise<unknown> = Promise.resolve();
  let flushing: Promise<void> | undefined;
  let revision = 0, needsBaseline = false;
  const started = Date.now();
  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = operations.then(operation);
    operations = result.catch(() => {});
    return result;
  }
  async function readState(): Promise<TabState> {
    return (await chrome.storage.local.get<{ activeTabFacts?: TabState }>("activeTabFacts")).activeTabFacts ?? { sessionSequence: 0, pending: [] };
  }
  async function session() {
    let { captureSessionId, tabRunStartedAt } = await chrome.storage.session.get<{ captureSessionId?: string; tabRunStartedAt?: number }>(["captureSessionId", "tabRunStartedAt"]);
    if (!captureSessionId || !tabRunStartedAt) {
      captureSessionId = crypto.randomUUID(); tabRunStartedAt = started;
      await chrome.storage.session.set({ captureSessionId, tabRunStartedAt });
    }
    return { captureSessionId, tabRunStartedAt };
  }
  async function write(eventKind: EventKind, tab: Tab | null, at: number, force = false) {
    const capture = await session(), state = await readState();
    const sameSession = state.captureSessionId === capture.captureSessionId;
    const current = { windowId: tab?.windowId ?? null, tabId: tab?.id ?? null, url: tab?.url ?? null, title: tab?.title ?? null };
    if (!force && sameSession && state.windowId === current.windowId && state.tabId === current.tabId && state.url === current.url && state.title === current.title) return;
    const sequence = sameSession ? state.sessionSequence + 1 : 1;
    const event: ActiveTabEvent = { ...current, eventId: crypto.randomUUID(), captureSessionId: capture.captureSessionId,
      sessionSequence: sequence, runStartedAtUtcMs: capture.tabRunStartedAt, observedAtUtcMs: at, eventKind };
    await chrome.storage.local.set({ activeTabFacts: { ...state, ...current, captureSessionId: capture.captureSessionId,
      sessionSequence: sequence, pending: [...state.pending, event],
      baselineSession: eventKind === "snapshot" ? capture.captureSessionId : state.baselineSession,
      needsBaseline: eventKind === "snapshot" ? false : state.needsBaseline } });
  }
  async function failed(detail: string) {
    needsBaseline = true;
    await serialize(async () => {
      const state = await readState(); state.needsBaseline = true;
      await chrome.storage.local.set({ activeTabFacts: state });
    });
    await coverage("active_tabs", "unavailable", detail);
  }
  function record(kind: EventKind, observe: () => Promise<Tab | null | undefined>, queued = false) {
    revision++;
    const at = Date.now();
    const observation = queued ? undefined : observe();
    // Attach the rejection immediately while retaining callback order in the queue.
    const result = observation?.then(value => ({ value }), error => ({ error }));
    void serialize(async () => {
      const observed = result ? await result : { value: await observe() };
      if ("error" in observed) throw observed.error;
      if (observed.value !== undefined) await write(kind, observed.value, at);
    }).catch(async error => {
      await failed("event_read_failed");
      console.error("Could not read active tab", error);
    });
  }
  function activeTab(window: chrome.windows.Window | undefined) {
    const tab = window?.tabs?.find(candidate => candidate.active);
    return tab?.incognito || tab?.id == null || tab.id < 0 ? null : tab;
  }
  chrome.windows.onFocusChanged.addListener(windowId => {
    if (windowId === chrome.windows.WINDOW_ID_NONE) record("window_blurred", async () => null);
    else record("window_focused", async () => activeTab(await chrome.windows.get(windowId, { populate: true })));
  });
  chrome.tabs.onActivated.addListener(({ tabId, windowId }) => record("tab_activated", async () => {
    const [window, tab] = await Promise.all([chrome.windows.get(windowId), chrome.tabs.get(tabId)]);
    if (window.focused && tab.windowId === windowId && tab.id === tabId && tabId >= 0) return tab.incognito ? null : tab;
  }));
  chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
    if (!tab.active || tab.incognito || tabId < 0 || (change.url === undefined && change.title === undefined)) return;
    const captured = { id: tabId, windowId: tab.windowId, url: change.url ?? tab.url, title: change.title ?? tab.title };
    record(change.url !== undefined ? "url_changed" : "title_changed", async () => {
      const window = await chrome.windows.get(captured.windowId);
      if (window.focused) return captured;
    });
  });
  chrome.tabs.onRemoved.addListener(tabId => record("tab_removed", async () => {
    const state = await readState();
    return state.tabId === tabId ? null : undefined;
  }, true));
  chrome.windows.onRemoved.addListener(windowId => record("window_removed", async () => {
    const state = await readState();
    return state.windowId === windowId ? null : undefined;
  }, true));

  async function snapshot(databaseId?: string) {
    await flush();
    await serialize(async () => {
      const state = await readState();
      if (databaseId && state.databaseId !== databaseId) {
        if (state.databaseId) await coverage("active_tabs", "unavailable", "database_replaced");
        await chrome.storage.session.set({ captureSessionId: crypto.randomUUID(), tabRunStartedAt: Date.now() });
        state.databaseId = databaseId; state.baselineSession = undefined;
        await chrome.storage.local.set({ activeTabFacts: state });
      }
    });
    for (let attempt = 0; attempt < 3; attempt++) {
      const version = revision;
      let windows: chrome.windows.Window[];
      try { windows = await chrome.windows.getAll({ populate: true }); }
      catch (error) { await failed("snapshot_failed"); throw error; }
      const at = Date.now(), tab = activeTab(windows.find(window => window.focused));
      const accepted = await serialize(async () => {
        if (version !== revision) return false;
        const state = await readState(), capture = await session();
        const changed = state.tabId !== (tab?.id ?? null) || state.windowId !== (tab?.windowId ?? null) ||
          state.url !== (tab?.url ?? null) || state.title !== (tab?.title ?? null);
        const baseline = needsBaseline || state.needsBaseline || state.baselineSession !== capture.captureSessionId;
        if (state.baselineSession && (baseline || changed)) {
          await coverage("active_tabs", "unavailable", baseline ? "baseline_reset" : "reconciliation_changed");
          await chrome.storage.session.set({ captureSessionId: crypto.randomUUID(), tabRunStartedAt: at });
        }
        await write("snapshot", tab, at, baseline || changed);
        needsBaseline = false;
        return true;
      });
      if (!accepted) continue;
      await flush();
      await coverage("active_tabs", "available", "baseline_received");
      return;
    }
    await failed("snapshot_raced");
  }
  async function drain() {
    while (true) {
      const { pending } = await serialize(readState);
      if (!pending.length) return;
      let count = 0, bytes = 0;
      while (count < pending.length) { const size = jsonBytes(pending[count]) + 1; if (bytes + size > batchBytesAvailable) break; bytes += size; count++; }
      if (!count) throw new Error("An active tab event exceeds the local collector message limit.");
      await sendBatch({ activeTabEvents: pending.slice(0, count) });
      await serialize(async () => {
        const state = await readState(); state.pending.splice(0, count);
        await chrome.storage.local.set({ activeTabFacts: state });
      });
    }
  }
  function flush(): Promise<void> { return flushing ??= drain().finally(() => { flushing = undefined; }); }
  return { snapshot, flush,
    async sync(databaseId: string) {
      const state = await serialize(readState), capture = await serialize(session);
      if (needsBaseline || state.needsBaseline || state.databaseId !== databaseId || state.baselineSession !== capture.captureSessionId) await snapshot(databaseId);
      else await flush();
    },
  };
}
