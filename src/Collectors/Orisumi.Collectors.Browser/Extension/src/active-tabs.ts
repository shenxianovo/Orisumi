import { batchBytesAvailable, jsonBytes, sendBatch } from "./native-host.js";

type EventKind = "snapshot" | "window_blurred" | "window_focused" | "tab_activated" | "url_changed";
type Tab = Pick<chrome.tabs.Tab, "id" | "windowId" | "url" | "title">;
type TabLocation = {
  windowId: number | null;
  tabId: number | null;
  url: string | null;
};

export type ActiveTabEvent = TabLocation & {
  eventId: string;
  captureSessionId: string;
  sessionSequence: number;
  observedAtUtcMs: number;
  eventKind: EventKind;
  title: string | null;
};

type TabState = Partial<TabLocation> & {
  captureSessionId?: string;
  sessionSequence: number;
  pending: ActiveTabEvent[];
};

export function observeActiveTabs() {
  let operations: Promise<void> = Promise.resolve();

  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = operations.then(operation);
    operations = result.then(() => {}, error => console.error("Could not record active tab", error));
    return result;
  }

  async function readState(): Promise<TabState> {
    const { activeTabState } = await chrome.storage.local.get<{ activeTabState?: TabState }>("activeTabState");
    return activeTabState ?? { sessionSequence: 0, pending: [] };
  }

  function record(eventKind: EventKind, observe: () => Promise<Tab | null | undefined>) {
    const observedAtUtcMs = Date.now();
    const observation = observe().catch(error => {
      console.warn("Could not read active tab", error);
      return undefined;
    });
    return serialize(async () => {
      const tab = await observation;
      if (tab === undefined) return;

      let { captureSessionId } = await chrome.storage.session.get<{ captureSessionId?: string }>("captureSessionId");
      if (!captureSessionId) {
        captureSessionId = crypto.randomUUID();
        await chrome.storage.session.set({ captureSessionId });
      }
      const state = await readState();
      const sameSession = state.captureSessionId === captureSessionId;
      const windowId = tab?.windowId ?? null;
      const tabId = tab?.id ?? null;
      const url = tab?.url ?? null;
      if (sameSession && state.windowId === windowId && state.tabId === tabId && state.url === url) return;

      const sessionSequence = sameSession ? state.sessionSequence + 1 : 1;
      const event: ActiveTabEvent = {
        eventId: crypto.randomUUID(), captureSessionId, sessionSequence, observedAtUtcMs,
        eventKind, windowId, tabId, url, title: tab?.title ?? null,
      };
      await chrome.storage.local.set({ activeTabState: {
        captureSessionId, sessionSequence, windowId, tabId, url, pending: [...state.pending, event],
      } });
    });
  }

  function activeTab(window: chrome.windows.Window | undefined) {
    const tab = window?.tabs?.find(candidate => candidate.active);
    return tab?.incognito || tab?.id == null || tab.id < 0 ? null : tab;
  }

  chrome.windows.onFocusChanged.addListener(windowId => {
    if (windowId === chrome.windows.WINDOW_ID_NONE) {
      void record("window_blurred", async () => null);
      return;
    }
    void record("window_focused", async () =>
      activeTab(await chrome.windows.get(windowId, { populate: true })) ?? undefined);
  });

  chrome.tabs.onActivated.addListener(({ tabId, windowId }) => {
    void record("tab_activated", async () => {
      const [window, tab] = await Promise.all([chrome.windows.get(windowId), chrome.tabs.get(tabId)]);
      if (window.focused && !tab.incognito && tab.windowId === windowId && tab.id === tabId && tabId >= 0) return tab;
    });
  });

  chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
    if (!tab.active || tab.incognito || tabId < 0 || change.url === undefined) return;
    void record("url_changed", async () => {
      const window = await chrome.windows.get(tab.windowId);
      if (window.focused) return { id: tabId, windowId: tab.windowId, url: change.url, title: change.title };
    });
  });

  return {
    snapshot: () => record("snapshot", async () => {
      const windows = await chrome.windows.getAll({ populate: true });
      return activeTab(windows.find(window => window.focused));
    }),
    async flush() {
      const { pending } = await serialize(readState);
      let start = 0;
      while (start < pending.length) {
        let end = start;
        let bytes = 0;
        while (end < pending.length) {
          const size = jsonBytes(pending[end]) + 1;
          if (bytes + size > batchBytesAvailable) break;
          bytes += size;
          end++;
        }
        if (end === start) throw new Error("An active tab event exceeds the local collector message limit.");
        await sendBatch({ activeTabEvents: pending.slice(start, end) });
        await serialize(async () => {
          const state = await readState();
          await chrome.storage.local.set({ activeTabState: { ...state, pending: state.pending.slice(end - start) } });
        });
        start = end;
      }
    },
  };
}
