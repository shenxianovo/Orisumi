const roundAlarm = "orisumi-persistent-round";
export const persistentSubscriptions = {
  history: ["onVisited", "onVisitRemoved"],
  bookmarks: ["onCreated", "onRemoved", "onChanged", "onMoved", "onChildrenReordered", "onImportBegan", "onImportEnded"],
  downloads: ["onCreated", "onChanged", "onErased"],
};

export class PersistentCapture {
  constructor(chrome, record) {
    this.chrome = chrome;
    this.record = record;
    this.running = undefined;
    this.enabled = false;
    this.roundPending = false;
    this.events = new Map();
    for (const event of persistentSubscriptions.history) {
      this.listen("history", event, (args, sequence) => {
        if (event === "onVisited" && typeof args[0]?.url === "string") this.events.set(`history:${args[0].url}`, { operation: "history.getVisits", args: [{ url: args[0].url }], sequence });
      });
    }
    for (const event of persistentSubscriptions.bookmarks) {
      this.listen("bookmarks", event, (_, sequence) => {
        if (event === "onImportBegan") this.importing ??= Promise.withResolvers();
        if (event === "onImportEnded") {
          this.importing?.resolve();
          this.importing = undefined;
        }
        this.events.set("bookmarks", { operation: "bookmarks.getTree", args: [], sequence });
      });
    }
    for (const event of persistentSubscriptions.downloads) {
      this.listen("downloads", event, (args, sequence) => {
        if (event !== "onErased" && Number.isInteger(args[0]?.id)) this.events.set(`downloads:${args[0].id}`, { operation: "downloads.search", args: [{ id: args[0].id }], sequence });
      });
    }
    this.chrome.alarms.onAlarm.addListener((alarm) => {
      if (alarm.name !== roundAlarm || !this.enabled || this.collecting || this.roundPending) return;
      this.roundPending = true;
      void this.resume().catch(console.error);
    });
  }

  start() {
    if (!this.enabled) {
      this.enabled = true;
      this.roundPending = true;
    }
    return this.resume();
  }

  listen(namespace, event, enqueue) {
    this.chrome[namespace][event].addListener((...callbackArgs) => {
      const { sequence } = this.record("callback", { operation: `${namespace}.${event}`, callbackArgs });
      enqueue(callbackArgs, sequence);
      void this.resume().catch(console.error);
    });
  }

  resume() {
    if (!this.enabled) return Promise.resolve();
    this.requested = true;
    this.running ??= this.work().finally(() => {
      this.running = undefined;
      if (this.requested) void this.resume().catch(console.error);
    });
    return this.running;
  }

  async work() {
    while (true) {
      this.requested = false;
      if (this.roundPending) {
        this.roundPending = false;
        this.collecting = true;
        try {
          await this.collect();
        } finally {
          this.collecting = false;
        }
      } else if (!await this.eventOnce() && !this.requested) return;
    }
  }

  async eventOnce(round) {
    const next = [...this.events].find(([key]) => key !== "bookmarks" || !this.importing);
    if (!next) return false;
    const [key, task] = next;
    this.events.delete(key);
    await this.query(task.operation, task.args, task.sequence, round);
    return true;
  }

  async collect() {
    const round = { id: crypto.randomUUID(), endTime: Date.now(), queryCount: 0, errors: [], urls: new Set(), unresolvedIntervals: [] };
    const started = this.record("status", {
      operation: "orisumi.persistentRound", round: round.id, state: "started",
      historyRange: { startTime: 0, endTime: round.endTime },
    });
    await started.committed;
    while (this.importing) await this.importing.promise;
    this.events.delete("bookmarks");
    await this.query("bookmarks.getTree", [], started.sequence, round);
    await this.eventOnce(round);
    await this.query("downloads.search", [{ limit: 0, orderBy: ["startTime"], startedBefore: new Date(round.endTime).toISOString() }], started.sequence, round);
    await this.eventOnce(round);
    await this.history(round, started.sequence);
    await this.record("status", {
      operation: "orisumi.persistentRound", round: round.id, state: "finished", queryCount: round.queryCount,
      discoveredUrls: round.urls.size, unresolvedIntervals: round.unresolvedIntervals, errors: round.errors,
    }).committed;
    await this.chrome.alarms.create(roundAlarm, { delayInMinutes: 5 });
  }

  async history(round, triggerSequence) {
    const pending = [{ startTime: 0, endTime: round.endTime, maxResults: 100 }];
    while (pending.length) {
      const interval = pending.pop();
      const result = await this.query("history.search", [{ text: "", ...interval }], triggerSequence, round);
      await this.eventOnce(round);
      const items = result.callbackArgs[0];
      if (result.error || !Array.isArray(items)) {
        round.unresolvedIntervals.push({ ...interval, sequence: result.sequence, reason: result.error ? "query-error" : "unexpected-result" });
        continue;
      }
      if (items.length >= interval.maxResults) {
        const { startTime, endTime, maxResults } = interval;
        if (endTime - startTime > 1) {
          const middle = startTime + Math.floor((endTime - startTime) / 2);
          pending.push({ startTime, endTime: middle, maxResults }, { startTime: middle, endTime, maxResults });
        } else if (maxResults < 2147483647) {
          pending.push({ ...interval, maxResults: Math.min(maxResults * 2, 2147483647) });
        } else {
          round.unresolvedIntervals.push({ ...interval, sequence: result.sequence, reason: "saturated" });
        }
      }
      for (const item of items) {
        if (typeof item.url !== "string" || round.urls.has(item.url)) continue;
        round.urls.add(item.url);
        await this.query("history.getVisits", [{ url: item.url }], result.sequence, round);
        await this.eventOnce(round);
      }
    }
  }

  async query(operation, args, triggerSequence, round) {
    const requestedAt = new Date().toISOString();
    const [namespace, method] = operation.split(".");
    if (round) round.queryCount++;
    const result = await new Promise((resolve) => {
      try {
        this.chrome[namespace][method](...args, (...callbackArgs) => {
          const error = this.chrome.runtime.lastError?.message;
          resolve({ callbackArgs, ...(error ? { error } : {}) });
        });
      } catch (error) {
        resolve({ callbackArgs: [], error: String(error), failurePhase: "invoke" });
      }
    });
    const observation = this.record("query", { operation, arguments: args, requestedAt, triggerSequence, ...(round ? { round: round.id } : {}), ...result });
    const { capture } = await observation.committed;
    if (result.error && round) round.errors.push({ sequence: observation.sequence, operation, capture });
    return { ...result, sequence: observation.sequence };
  }
}
