const prefixes = { live: "pending:", persistent: "persistent:" };
const nativeLimit = 64 * 1024 * 1024;
const fragmentLength = 1024 * 1024;
const sizeError = "Message exceeded maximum allowed size of 64MiB.";

function base64(bytes) {
  const parts = [];
  for (let offset = 0; offset < bytes.length; offset += 32768)
    parts.push(String.fromCharCode(...bytes.subarray(offset, offset + 32768)));
  return btoa(parts.join(""));
}

function promise() {
  let resolve;
  const value = new Promise((complete) => { resolve = complete; });
  return { value, resolve };
}

function ticket() {
  return { persisted: promise(), committed: promise() };
}

export class Delivery {
  constructor(chrome, report = console.error) {
    this.chrome = chrome;
    this.report = report;
    this.waiting = [];
    this.tickets = new Map();
    this.lanes = Object.fromEntries(Object.entries(prefixes).map(([name, prefix]) => [name, {
      prefix, keys: [], port: undefined, inFlight: undefined, acknowledged: undefined, blocked: false,
    }]));
    this.instance = undefined;
    this.initialized = promise();
    this.running = undefined;
    this.requested = false;
  }

  enqueue(observation, channel = "live") {
    const lane = this.lanes[channel];
    const key = `${lane.prefix}${observation.run}:${String(observation.sequence).padStart(16, "0")}`;
    const completion = ticket();
    this.tickets.set(key, completion);
    this.waiting.push({ observation, channel, key, completion });
    void this.resume();
    return { persisted: completion.persisted.value, committed: completion.committed.value };
  }

  replayed(channel) {
    void this.resume();
    return this.initialized.value.then(() => this.lanes[channel].replay);
  }

  retry() {
    for (const lane of Object.values(this.lanes)) lane.blocked = false;
    return this.resume();
  }

  resume() {
    this.requested = true;
    if (this.running) return this.running;
    this.running = this.flush().catch((error) => {
      this.requested = false;
      this.report(error);
    }).finally(() => {
      this.running = undefined;
      if (this.requested) void this.resume();
    });
    return this.running;
  }

  async initialize() {
    if (this.instance) return;
    const storage = this.chrome.storage.local;
    const stored = await storage.get("instance");
    const instance = stored.instance ?? crypto.randomUUID();
    if (!stored.instance) await storage.set({ instance });
    const keys = await storage.getKeys();
    for (const lane of Object.values(this.lanes)) {
      lane.keys = keys.filter((key) => key.startsWith(lane.prefix)).sort();
      lane.replay = Promise.all(lane.keys.map((key) => {
        const completion = ticket();
        this.tickets.set(key, completion);
        completion.persisted.resolve();
        return completion.committed.value;
      })).then(() => {});
    }
    this.instance = instance;
    this.initialized.resolve();
  }

  async flush() {
    while (this.requested) {
      this.requested = false;
      await this.initialize();
      const storage = this.chrome.storage.local;
      const waiting = this.waiting;
      this.waiting = [];
      for (const item of waiting) {
        try {
          const observation = { format: "orisumi-browser-v1", instance: this.instance, ...item.observation };
          await storage.set({ [item.key]: JSON.stringify(observation) });
          this.lanes[item.channel].keys.push(item.key);
          item.completion.persisted.resolve();
        } catch (error) {
          this.waiting.push(item);
          this.report(error);
        }
      }
      await Promise.all(Object.values(this.lanes).map((lane) => this.flushLane(lane).catch((error) => { this.report(error); })));
    }
  }

  async flushLane(lane) {
    const storage = this.chrome.storage.local;
    if (lane.acknowledged) {
      const key = lane.acknowledged;
      await storage.remove(key);
      lane.keys = lane.keys.filter((candidate) => candidate !== key);
      this.tickets.delete(key);
      lane.acknowledged = undefined;
      lane.inFlight = undefined;
    }
    if (lane.inFlight || lane.blocked || !lane.keys.length) return;
    const key = lane.keys[0];
    const stored = await storage.get(key);
    if (!stored[key]) throw new Error(`暂存观测缺失：${key}`);
    if (lane.blocked) return;
    const text = typeof stored[key] === "string" ? stored[key] : JSON.stringify(stored[key]);
    const observation = JSON.parse(text);
    if (!lane.port) this.connect(lane);
    const port = lane.port;
    const flight = { key, observation, sent: false };
    lane.inFlight = flight;
    void this.send(lane, port, flight, text).catch((error) => {
      if (lane.port === port && lane.inFlight === flight) this.disconnect(lane);
      this.report(error);
    });
  }

  async send(lane, port, flight, text) {
    const bytes = new TextEncoder().encode(text);
    if (bytes.length <= nativeLimit) {
      try {
        port.postMessage(flight.observation);
        flight.sent = true;
        return;
      } catch (error) {
        if (error.message !== sizeError) throw error;
      }
    }
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
    const hash = `sha256-${Array.from(digest, (byte) => byte.toString(16).padStart(2, "0")).join("")}`;
    const { instance, run, sequence } = flight.observation;
    for (let offset = 0; offset < bytes.length; offset += fragmentLength) {
      if (lane.port !== port || lane.inFlight !== flight) return;
      const frame = {
        format: "orisumi-browser-fragment-v1", instance, run, sequence,
        byteLength: bytes.length, hash, offset, data: base64(bytes.subarray(offset, offset + fragmentLength)),
      };
      if (new TextEncoder().encode(JSON.stringify(frame)).length > nativeLimit)
        throw new Error("分片编码后的 Native 消息超过 64 MiB");
      port.postMessage(frame);
      if (offset + fragmentLength >= bytes.length) flight.sent = true;
      else await new Promise((resolve) => setTimeout(resolve, 0));
    }
  }

  connect(lane) {
    const port = this.chrome.runtime.connectNative("dev.orisumi.browser");
    lane.port = port;
    port.onMessage.addListener((message) => {
      if (lane.port !== port) return;
      const sent = lane.inFlight?.observation;
      if (!sent || !lane.inFlight.sent || message?.format !== "orisumi-browser-ack-v1" || message.instance !== sent.instance
        || message.run !== sent.run || message.sequence !== sent.sequence
        || typeof message.capture !== "string" || !/^sha256-[0-9a-f]{64}$/.test(message.capture)) {
        this.report(new Error("本机回执与正在发送的观测不符，保留暂存"));
        return;
      }
      lane.acknowledged = lane.inFlight.key;
      this.tickets.get(lane.inFlight.key)?.committed.resolve({ capture: message.capture });
      void this.resume();
    });
    port.onDisconnect.addListener(() => {
      const error = this.chrome.runtime.lastError?.message;
      if (lane.port !== port) return;
      lane.port = undefined;
      lane.inFlight = undefined;
      lane.blocked = true;
      this.report(new Error(error ?? "本机接收器已断开，观测保留在暂存中"));
    });
  }

  disconnect(lane) {
    const port = lane.port;
    lane.port = undefined;
    lane.inFlight = undefined;
    lane.blocked = true;
    port?.disconnect();
  }
}
