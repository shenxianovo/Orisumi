import { Delivery } from "./delivery.js";
import { PersistentCapture, persistentSubscriptions } from "./persistent.js";

const run = crypto.randomUUID();
let sequence = 0;
const delivery = new Delivery(chrome);
const retryAlarm = "deliver-observations";
const subscriptions = {
  tabs: ["onCreated", "onUpdated", "onMoved", "onActivated", "onHighlighted", "onDetached", "onAttached", "onRemoved", "onReplaced", "onZoomChange"],
  windows: ["onCreated", "onRemoved", "onFocusChanged", "onBoundsChanged"],
};

function observe(kind, body, channel = "live") {
  const observation = { run, sequence: ++sequence, observedAt: new Date().toISOString(), kind, ...body };
  return { sequence: observation.sequence, ...delivery.enqueue(observation, channel) };
}

function query(operation, args, triggerSequence) {
  const requestedAt = new Date().toISOString();
  const [namespace, method] = operation.split(".");
  chrome[namespace][method](...args, (...callbackArgs) => {
    const error = chrome.runtime.lastError?.message;
    observe("query", { operation, arguments: args, requestedAt, triggerSequence, callbackArgs, ...(error ? { error } : {}) });
  });
}

function snapshot(triggerSequence) {
  query("windows.getAll", [{ populate: false, windowTypes: ["normal", "popup"] }], triggerSequence);
  query("tabs.query", [{ active: true }], triggerSequence);
}

for (const [namespace, events] of Object.entries(subscriptions)) {
  for (const event of events) {
    chrome[namespace][event].addListener((...callbackArgs) => {
      const triggerSequence = observe("callback", { operation: `${namespace}.${event}`, callbackArgs }).sequence;
      if (namespace === "tabs" && event === "onActivated") query("tabs.get", [callbackArgs[0].tabId], triggerSequence);
      if (namespace === "windows") snapshot(triggerSequence);
    });
  }
}

const persistent = new PersistentCapture(chrome, (kind, body) => observe(kind, body, "persistent"));
chrome.runtime.onStartup.addListener(() => { void delivery.retry(); });
chrome.alarms.onAlarm.addListener((alarm) => { if (alarm.name === retryAlarm) void delivery.retry(); });

const started = observe("started", {
  extensionId: chrome.runtime.id,
  manifest: chrome.runtime.getManifest(),
  userAgent: navigator.userAgent,
  subscriptions: { ...subscriptions, ...persistentSubscriptions },
  windowTypes: ["normal", "popup"],
  windowIdNone: chrome.windows.WINDOW_ID_NONE,
}).sequence;
snapshot(started);
query("runtime.getPlatformInfo", [], started);
query("permissions.getAll", [], started);
void chrome.alarms.create(retryAlarm, { periodInMinutes: 1 }).catch(console.error);
void delivery.replayed("persistent").then(() => persistent.start()).catch(console.error);
