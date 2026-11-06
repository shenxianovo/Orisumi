import test from "node:test";
import assert from "node:assert/strict";
import { PersistentCapture } from "./persistent.js";

const settle = () => new Promise((resolve) => setImmediate(resolve));

function fixture({ answer = () => ({ callbackArgs: [[]] }), hold = () => false } = {}) {
  const listeners = new Map();
  const calls = [];
  const records = [];
  const alarms = [];
  const receipts = new Map();
  const chrome = { runtime: {} };
  const event = (name) => ({ addListener: (listener) => listeners.set(name, listener) });
  for (const [namespace, names] of Object.entries({
    history: ["onVisited", "onVisitRemoved"],
    bookmarks: ["onCreated", "onRemoved", "onChanged", "onMoved", "onChildrenReordered", "onImportBegan", "onImportEnded"],
    downloads: ["onCreated", "onChanged", "onErased"],
  })) {
    chrome[namespace] = {};
    for (const name of names) chrome[namespace][name] = event(`${namespace}.${name}`);
  }
  chrome.alarms = { onAlarm: event("alarms.onAlarm"), create: async (name, options) => alarms.push({ name, options }) };
  for (const operation of ["history.search", "history.getVisits", "bookmarks.getTree", "downloads.search"]) {
    const [namespace, method] = operation.split(".");
    chrome[namespace][method] = (...args) => {
      const callback = args.pop();
      calls.push({ operation, args: structuredClone(args) });
      const response = answer(operation, args);
      if (response.pending) return;
      if (response.throw) throw response.throw;
      if (response.error) chrome.runtime.lastError = { message: response.error };
      callback(...response.callbackArgs);
      delete chrome.runtime.lastError;
    };
  }
  const record = (kind, body) => {
    const value = { sequence: records.length + 1, kind, ...structuredClone(body) };
    records.push(value);
    const receipt = Promise.withResolvers();
    receipts.set(value.sequence, receipt);
    if (!hold(value)) receipt.resolve({ capture: `sha256-${String(value.sequence).padStart(64, "0")}` });
    return { sequence: value.sequence, persisted: Promise.resolve(), committed: receipt.promise };
  };
  const capture = new PersistentCapture(chrome, record);
  return {
    capture, calls, records, alarms,
    emit: (operation, ...args) => listeners.get(operation)(...args),
    commit: (sequence) => receipts.get(sequence).resolve({ capture: `sha256-${String(sequence).padStart(64, "0")}` }),
  };
}

test("先保存书签与下载基线，完整回执后推进并在完成回执后安排下一轮", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const tree = [{ id: "root", children: [{ id: "one", unknown: { raw: "保留" } }] }];
  const f = fixture({
    answer: (operation) => ({ callbackArgs: [operation === "bookmarks.getTree" ? tree : []] }),
    hold: (record) => record.operation === "bookmarks.getTree" || record.state === "finished",
  });
  const running = f.capture.start();
  await settle();
  assert.deepEqual(f.calls.map((call) => call.operation), ["bookmarks.getTree"]);
  const bookmark = f.records.find((record) => record.operation === "bookmarks.getTree");
  assert.deepEqual(bookmark.callbackArgs, [tree]);
  f.commit(bookmark.sequence);
  await settle();
  assert.deepEqual(f.calls.map((call) => call.operation), ["bookmarks.getTree", "downloads.search", "history.search"]);
  assert.deepEqual(f.calls[1].args, [{ limit: 0, orderBy: ["startTime"], startedBefore: "1970-01-01T00:00:00.004Z" }]);
  assert.deepEqual(f.calls[2].args, [{ text: "", startTime: 0, endTime: 4, maxResults: 100 }]);
  const started = f.records.find((record) => record.state === "started");
  const finished = f.records.find((record) => record.state === "finished");
  assert.equal(finished.round, started.round);
  assert.equal(finished.queryCount, 3);
  assert.equal(f.alarms.length, 0);
  f.commit(finished.sequence);
  await running;
  assert.deepEqual(f.alarms, [{ name: "orisumi-persistent-round", options: { delayInMinutes: 5 } }]);
});

test("按请求区间覆盖同刻与微秒访问，原数组饱和不受重复 URL 或窗口外 lastVisitTime 干扰", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const full = Array.from({ length: 100 }, (_, index) => ({ id: String(Math.floor(index / 2)), url: `https://example.test/${Math.floor(index / 2)}`, lastVisitTime: index < 2 ? 5 : index < 4 ? 1.25 : 1.999 }));
  const more = [...full, { id: "extra", url: "https://example.test/extra", lastVisitTime: 1 }];
  const visits = [{ visitId: "outside", visitTime: 9, unknown: { raw: true } }];
  const f = fixture({
    answer: (operation, args) => {
      if (operation === "history.getVisits") return { callbackArgs: [visits] };
      if (operation !== "history.search") return { callbackArgs: [[]] };
      const { startTime: start, endTime: end, maxResults: limit } = args[0];
      if (start === 2) return { callbackArgs: [[]] };
      if (end === 1) return { callbackArgs: [[full[0]]] };
      return { callbackArgs: [limit === 200 ? more : full] };
    }
  });
  await f.capture.start();
  assert.deepEqual(f.calls.filter((call) => call.operation === "history.search").map((call) => {
    const args = call.args[0];
    return [args.startTime, args.endTime, args.maxResults];
  }), [[0, 4, 100], [2, 4, 100], [0, 2, 100], [1, 2, 100], [1, 2, 200], [0, 1, 100]]);
  const requests = f.calls.filter((call) => call.operation === "history.getVisits");
  assert.equal(requests.length, 51);
  assert.equal(requests.filter((call) => call.args[0].url === full[0].url).length, 1);
  const queries = f.records.filter((record) => record.kind === "query");
  assert.deepEqual(queries.filter((record) => record.operation === "history.search").map((record) => record.callbackArgs[0].length), [100, 0, 100, 100, 101, 1]);
  assert.deepEqual(queries.find((record) => record.operation === "history.getVisits").callbackArgs, [visits]);
  assert.equal(f.records.at(-1).discoveredUrls, 51);
  assert.deepEqual(f.records.at(-1).unresolvedIntervals, []);
});

test("构造即保留全部原回调，启动门槛前不查询，同 URL 与 ID 的后续请求合并", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const f = fixture({ hold: (record) => record.operation === "bookmarks.getTree" });
  const visited = { url: "https://example.test/visited", extra: { retained: true } };
  f.emit("history.onVisited", visited);
  f.emit("history.onVisited", visited);
  f.emit("history.onVisitRemoved", { allHistory: false, urls: [visited.url] });
  f.emit("downloads.onCreated", { id: 7, state: "in_progress" });
  f.emit("downloads.onChanged", { id: 7, state: { previous: "in_progress", current: "complete" } });
  f.emit("downloads.onErased", 9);
  assert.equal(f.calls.length, 0);
  assert.equal(f.records.length, 6);
  assert.deepEqual(f.records[0].callbackArgs, [visited]);
  const running = f.capture.start();
  await settle();
  f.commit(f.records.find((record) => record.operation === "bookmarks.getTree").sequence);
  await running;
  assert.equal(f.records.filter((record) => record.kind === "callback").length, 6);
  assert.deepEqual(f.calls.filter((call) => call.operation === "history.getVisits").map((call) => call.args), [[{ url: visited.url }]]);
  assert.deepEqual(f.calls.filter((call) => call.operation === "downloads.search" && "id" in call.args[0]).map((call) => call.args), [[{ id: 7 }]]);
  assert.equal(f.records.find((record) => record.operation === "history.getVisits").triggerSequence, 2);
  const baseline = f.calls.findIndex((call) => call.operation === "downloads.search" && "limit" in call.args[0]);
  assert.ok(f.calls.findIndex((call) => call.operation === "history.getVisits") < baseline);
});

test("导入期间保留书签回调，结束后取整树，进行中新增变化只合并后续重读", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const f = fixture({ hold: (record) => record.operation === "bookmarks.getTree" });
  f.emit("bookmarks.onImportBegan");
  const running = f.capture.start();
  f.emit("bookmarks.onCreated", "a", { title: "first" });
  f.emit("bookmarks.onCreated", "b", { title: "second" });
  await settle();
  assert.equal(f.calls.length, 0);
  f.emit("bookmarks.onImportEnded");
  await settle();
  assert.equal(f.calls.length, 1);
  f.emit("bookmarks.onChanged", "a", { title: "changed" });
  f.emit("bookmarks.onRemoved", "b", { node: { id: "b" } });
  f.emit("bookmarks.onMoved", "a", { parentId: "root" });
  f.emit("bookmarks.onChildrenReordered", "root", { childIds: ["a"] });
  f.commit(f.records.find((record) => record.operation === "bookmarks.getTree").sequence);
  await settle();
  assert.equal(f.calls.filter((call) => call.operation === "bookmarks.getTree").length, 2);
  const trees = f.records.filter((record) => record.operation === "bookmarks.getTree");
  f.commit(trees[1].sequence);
  await running;
  assert.equal(f.records.filter((record) => record.kind === "callback").length, 8);
  assert.equal(f.calls.filter((call) => call.operation === "bookmarks.getTree").length, 2);
  assert.equal(trees[1].triggerSequence, f.records.find((record) => record.operation === "bookmarks.onChildrenReordered").sequence);
});

test("周期只在完成后安排，重复启动和活动轮次 alarm 不重叠，新 Worker 以新 T 重扫", async (context) => {
  let now = 4;
  context.mock.method(Date, "now", () => now);
  const f = fixture({ hold: (record) => record.operation === "history.search" });
  f.emit("alarms.onAlarm", { name: "orisumi-persistent-round" });
  assert.equal(f.records.length, 0);
  const first = f.capture.start();
  assert.equal(f.capture.start(), first);
  await settle();
  f.emit("alarms.onAlarm", { name: "orisumi-persistent-round" });
  assert.equal(f.records.filter((record) => record.state === "started").length, 1);
  assert.equal(f.records.filter((record) => record.state === "finished").length, 0);
  f.commit(f.records.find((record) => record.operation === "history.search").sequence);
  await first;
  now = 8;
  f.emit("alarms.onAlarm", { name: "unrelated" });
  f.emit("alarms.onAlarm", { name: "orisumi-persistent-round" });
  const second = f.capture.start();
  await settle();
  const searches = f.records.filter((record) => record.operation === "history.search");
  assert.deepEqual(searches.map((record) => record.arguments[0].endTime), [4, 8]);
  f.emit("alarms.onAlarm", { name: "orisumi-persistent-round" });
  f.commit(searches[1].sequence);
  await second;
  assert.equal(f.records.filter((record) => record.state === "finished").length, 2);
  assert.equal(f.alarms.length, 2);
  now = 12;
  const restarted = fixture();
  await restarted.capture.start();
  assert.deepEqual(restarted.records.find((record) => record.state === "started").historyRange, { startTime: 0, endTime: 12 });
  const rounds = [...f.records, ...restarted.records].filter((record) => record.state === "started").map((record) => record.round);
  assert.equal(new Set(rounds).size, 3);
});

test("调用抛错与 API 错误保留实际响应，未解决区间和错误原件进入收尾，其余请求继续", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const full = Array.from({ length: 100 }, (_, index) => ({ url: `https://example.test/${index}` }));
  const f = fixture({
    answer: (operation, args) => {
      if (operation === "bookmarks.getTree") return { throw: new Error("invoke failed") };
      if (operation === "downloads.search") return { callbackArgs: [{ partial: "untouched" }], error: "downloads denied" };
      if (operation === "history.getVisits") return args[0].url === full[0].url ? { callbackArgs: [], error: "visits failed" } : { callbackArgs: [[]] };
      const interval = args[0];
      if (interval.startTime === 2) return { callbackArgs: [], error: "search failed" };
      if (interval.maxResults === 200) return { throw: new Error("limit rejected") };
      return { callbackArgs: [interval.endTime === 1 ? [] : full] };
    }
  });
  await f.capture.start();
  const errors = f.records.filter((record) => record.error);
  assert.equal(errors.length, 5);
  assert.deepEqual(errors.find((record) => record.operation === "downloads.search").callbackArgs, [{ partial: "untouched" }]);
  assert.equal(errors.find((record) => record.operation === "bookmarks.getTree").failurePhase, "invoke");
  const finished = f.records.at(-1);
  assert.deepEqual(finished.errors.map((error) => error.sequence), errors.map((record) => record.sequence));
  assert.ok(finished.errors.every((error) => /^sha256-[0-9a-f]{64}$/.test(error.capture)));
  assert.deepEqual(finished.unresolvedIntervals.map(({ startTime, endTime, maxResults, reason }) => [startTime, endTime, maxResults, reason]), [[2, 4, 100, "query-error"], [1, 2, 200, "query-error"]]);
  assert.deepEqual(f.calls.at(-1).args, [{ text: "", startTime: 0, endTime: 1, maxResults: 100 }]);
  assert.equal(f.records.filter((record) => record.operation === "history.getVisits" && !record.error).length, 99);
});

test("未确认响应留下本轮前缀，新实例从新时间重扫而不替旧轮补完成", async (context) => {
  let now = 4;
  context.mock.method(Date, "now", () => now);
  const interrupted = fixture({ hold: (record) => record.operation === "bookmarks.getTree" });
  void interrupted.capture.start();
  await settle();
  assert.deepEqual(interrupted.records.map((record) => record.kind), ["status", "query"]);
  assert.equal(interrupted.alarms.length, 0);
  now = 8;
  const next = fixture();
  await next.capture.start();
  assert.notEqual(next.records[0].round, interrupted.records[0].round);
  assert.deepEqual(next.records[0].historyRange, { startTime: 0, endTime: 8 });
  assert.equal(interrupted.records.some((record) => record.state === "finished"), false);
  assert.equal(next.records.at(-1).state, "finished");
});

test("查询尚未返回时没有虚构 query 或完成，下一实例独立开始", async (context) => {
  let now = 4;
  context.mock.method(Date, "now", () => now);
  const old = fixture({ answer: (operation) => operation === "history.search" ? { pending: true } : { callbackArgs: [[]] } });
  void old.capture.start();
  await settle();
  assert.equal(old.calls.at(-1).operation, "history.search");
  assert.equal(old.records.some((record) => record.operation === "history.search"), false);
  assert.equal(old.records.some((record) => record.state === "finished"), false);
  now = 8;
  const next = fixture();
  await next.capture.start();
  assert.equal(next.records[0].historyRange.endTime, 8);
  assert.notEqual(next.records[0].round, old.records[0].round);
});

test("搜索之后删除仍保留搜索与删除原回调，空 visits 不改写为查询失败", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const item = { id: "one", url: "https://example.test/deleted", lastVisitTime: 2 };
  const f = fixture({
    answer: (operation) => ({ callbackArgs: [operation === "history.search" ? [item] : []] }),
    hold: (record) => record.operation === "history.search",
  });
  const running = f.capture.start();
  await settle();
  f.emit("history.onVisitRemoved", { allHistory: false, urls: [item.url] });
  const search = f.records.find((record) => record.operation === "history.search");
  f.commit(search.sequence);
  await running;
  assert.deepEqual(search.callbackArgs, [[item]]);
  const visits = f.records.find((record) => record.operation === "history.getVisits");
  assert.deepEqual(visits.callbackArgs, [[]]);
  assert.equal("error" in visits, false);
  assert.deepEqual(f.records.at(-1).errors, []);
  assert.equal(f.records.filter((record) => record.operation === "history.onVisitRemoved").length, 1);
});

test("不断到来的事件查询每次得到机会，事件回执后仍推进下一项基线", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const f = fixture({ hold: (record) => record.operation === "bookmarks.getTree" || (record.operation === "downloads.search" && record.arguments[0].id === 7) });
  const running = f.capture.start();
  await settle();
  f.emit("downloads.onChanged", { id: 7, state: { current: "in_progress" } });
  f.commit(f.records.find((record) => record.operation === "bookmarks.getTree").sequence);
  await settle();
  f.emit("downloads.onChanged", { id: 7, state: { current: "complete" } });
  f.emit("downloads.onChanged", { id: 8, exists: { current: true } });
  const first = f.records.find((record) => record.operation === "downloads.search");
  f.commit(first.sequence);
  await settle();
  assert.deepEqual(f.calls.slice(0, 4).map((call) => call.operation === "bookmarks.getTree" ? "tree" : call.args[0].id ?? "baseline"), ["tree", 7, "baseline", 7]);
  const second = f.records.filter((record) => record.operation === "downloads.search" && record.arguments[0].id === 7)[1];
  f.commit(second.sequence);
  await running;
  assert.equal(f.records.filter((record) => record.operation === "downloads.onChanged").length, 3);
  assert.equal(f.calls.filter((call) => call.operation === "history.search").length, 1);
});

test("无需后续查询的回调紧接新访问时，空闲切换不会遗留新请求", async (context) => {
  context.mock.method(Date, "now", () => 4);
  const f = fixture();
  await f.capture.start();
  f.emit("history.onVisitRemoved", { allHistory: false, urls: [] });
  f.emit("history.onVisited", { url: "https://example.test/new" });
  await settle();
  assert.deepEqual(f.calls.at(-1), { operation: "history.getVisits", args: [{ url: "https://example.test/new" }] });
});
