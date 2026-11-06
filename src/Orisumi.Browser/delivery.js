const pendingPrefix = "pending:";

export class Delivery {
  constructor(chrome, report = console.error) {
    this.chrome = chrome;
    this.report = report;
    this.waiting = [];
    this.keys = [];
    this.instance = undefined;
    this.port = undefined;
    this.inFlight = undefined;
    this.acknowledged = undefined;
    this.blocked = false;
    this.running = undefined;
    this.requested = false;
  }

  enqueue(observation) {
    this.waiting.push(observation);
    return this.resume();
  }

  retry() {
    this.blocked = false;
    return this.resume();
  }

  resume() {
    this.requested = true;
    if (this.running) return this.running;
    this.running = this.flush().catch((error) => {
      this.requested = false;
      this.report(error);
    }).finally(() => { this.running = undefined; });
    return this.running;
  }

  async flush() {
    while (this.requested) {
      this.requested = false;
      const storage = this.chrome.storage.local;
      if (!this.instance) {
        const stored = await storage.get("instance");
        const instance = stored.instance ?? crypto.randomUUID();
        if (!stored.instance) await storage.set({ instance });
        this.keys = (await storage.getKeys()).filter((key) => key.startsWith(pendingPrefix)).sort();
        this.instance = instance;
      }
      while (this.waiting.length) {
        const observation = this.waiting[0];
        const key = `${pendingPrefix}${observation.run}:${String(observation.sequence).padStart(16, "0")}`;
        try {
          await storage.set({ [key]: { format: "orisumi-browser-v1", instance: this.instance, ...observation } });
        } catch (error) {
          this.report(error);
          break;
        }
        this.keys.push(key);
        this.waiting.shift();
      }
      if (this.acknowledged) {
        const key = this.acknowledged;
        await storage.remove(key);
        this.keys = this.keys.filter((candidate) => candidate !== key);
        this.acknowledged = undefined;
        this.inFlight = undefined;
      }
      if (this.inFlight || this.blocked || !this.keys.length) continue;
      const key = this.keys[0];
      const stored = await storage.get(key);
      if (!stored[key]) throw new Error(`暂存观测缺失：${key}`);
      if (this.blocked) continue;
      if (!this.port) this.connect();
      this.inFlight = { key, observation: stored[key] };
      try {
        this.port.postMessage(stored[key]);
      } catch (error) {
        this.disconnect();
        throw error;
      }
    }
  }

  connect() {
    const port = this.chrome.runtime.connectNative("dev.orisumi.browser");
    this.port = port;
    port.onMessage.addListener((message) => {
      if (this.port !== port) return;
      const sent = this.inFlight?.observation;
      if (!sent || message?.format !== "orisumi-browser-ack-v1" || message.instance !== sent.instance
        || message.run !== sent.run || message.sequence !== sent.sequence
        || typeof message.capture !== "string" || !/^sha256-[0-9a-f]{64}$/.test(message.capture)) {
        this.report(new Error("本机回执与正在发送的观测不符，保留暂存"));
        return;
      }
      this.acknowledged = this.inFlight.key;
      void this.resume();
    });
    port.onDisconnect.addListener(() => {
      const error = this.chrome.runtime.lastError?.message;
      if (this.port !== port) return;
      this.port = undefined;
      this.inFlight = undefined;
      this.blocked = true;
      this.report(new Error(error ?? "本机接收器已断开，观测保留在暂存中"));
    });
  }

  disconnect() {
    const port = this.port;
    this.port = undefined;
    this.inFlight = undefined;
    this.blocked = true;
    port?.disconnect();
  }
}
