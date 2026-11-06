import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { Delivery } from "./delivery.js";

const run = "3ed5f16a-88b6-4ad8-806f-741663f22da3";
const observation = (sequence, overrides = {}) => ({ run, sequence, observedAt: "2026-11-06T13:00:00.000Z", kind: "callback", operation: "tabs.onActivated", callbackArgs: [{ tabId: 17, windowId: 3 }], ...overrides });
const pending = (state) => Object.keys(state).filter((key) => key.startsWith("pending:"));
const storedObservation = (value) => typeof value === "string" ? JSON.parse(value) : value;
const queued = (state) => Object.keys(state).filter((key) => key.startsWith("pending:") || key.startsWith("persistent:"));
async function queue(delivery, observation, lane) {
  const ticket = delivery.enqueue(observation, lane);
  await delivery.resume();
  return ticket;
}
const ack = (sent) => ({ format: "orisumi-browser-ack-v1", instance: sent.instance, run: sent.run, sequence: sent.sequence, capture: `sha256-${"a".repeat(64)}` });

async function until(condition) {
  for (let attempt = 0; attempt < 5000; attempt++) {
    if (condition()) return;
    await new Promise((resolve) => setTimeout(resolve, 1));
  }
  assert.fail("等待可见结果超时");
}

function fixture(state = {}, post) {
  const ports = [];
  const errors = [];
  const storage = {
    get: async (key) => ({ [key]: structuredClone(state[key]) }),
    getKeys: async () => Object.keys(state),
    set: async (values) => Object.assign(state, structuredClone(values)),
    remove: async (key) => { delete state[key]; },
  };
  const runtime = {
    connectNative: (name) => {
      assert.equal(name, "dev.orisumi.browser");
      let onMessage;
      let onDisconnect;
      const port = {
        sent: [],
        onMessage: { addListener: (callback) => { onMessage = callback; } },
        onDisconnect: { addListener: (callback) => { onDisconnect = callback; } },
        postMessage: (value) => {
          post?.(port, value);
          assert.ok(queued(state).some((key) => { const saved = storedObservation(state[key]); return saved.run === value.run && saved.sequence === value.sequence; }));
          port.sent.push(structuredClone(value));
        },
        receive: (value) => onMessage(value),
        disconnect: () => onDisconnect(),
      };
      ports.push(port);
      return port;
    },
  };
  const delivery = new Delivery({ storage: { local: storage }, runtime }, (error) => errors.push(String(error)));
  return { delivery, storage, ports, errors, state };
}

test("原件回执前保留暂存，逐条发送且忽略不匹配的回执", async () => {
  const { delivery, ports, state, errors } = fixture();
  await Promise.all([queue(delivery, observation(1)), queue(delivery, observation(2))]);
  assert.equal(pending(state).length, 2);
  assert.equal(ports[0].sent.length, 1);
  ports[0].receive({ ...ack(ports[0].sent[0]), sequence: 2 });
  await delivery.resume();
  assert.equal(pending(state).length, 2);
  assert.equal(errors.length, 1);
  ports[0].receive(ack(ports[0].sent[0]));
  await delivery.resume();
  assert.equal(pending(state).length, 1);
  assert.equal(ports[0].sent[1].sequence, 2);
  ports[0].receive(ack(ports[0].sent[1]));
  await delivery.resume();
  assert.equal(pending(state).length, 0);
});

test("接收器断开后保留新事件，Worker 重建可重放旧运行和新运行", async () => {
  const first = fixture();
  await queue(first.delivery, observation(8));
  first.ports[0].disconnect();
  await queue(first.delivery, observation(9));
  assert.equal(first.ports.length, 1);
  const restarted = fixture(first.state);
  await queue(restarted.delivery, observation(1, { run: "f1ed7dd1-2730-43da-9844-1c37b1ee87df" }));
  const port = restarted.ports[0];
  for (let index = 0; index < 3; index++) {
    port.receive(ack(port.sent[index]));
    await restarted.delivery.resume();
  }
  assert.equal(port.sent[0].instance, first.ports[0].sent[0].instance);
  assert.deepEqual(port.sent.map((sent) => sent.sequence), [8, 9, 1]);
  assert.equal(pending(first.state).length, 0);
});

test("暂存写入失败时不发送，重试保存当前内存中的事件", async () => {
  const { delivery, storage, ports, state, errors } = fixture();
  const set = storage.set;
  storage.set = async (values) => {
    if (pending(values).length) throw new Error("disk full");
    await set(values);
  };
  await queue(delivery, observation(1));
  assert.equal(ports.length, 0);
  assert.equal(pending(state).length, 0);
  assert.match(errors[0], /disk full/);
  storage.set = set;
  await delivery.retry();
  assert.equal(ports[0].sent[0].sequence, 1);
  assert.equal(pending(state).length, 1);
});

test("断连重试发送未确认的同一份观测，旧端口回执不能清除它", async () => {
  const { delivery, ports, state } = fixture();
  await queue(delivery, observation(7));
  const original = ports[0].sent[0];
  ports[0].disconnect();
  await delivery.retry();
  assert.deepEqual(ports[1].sent[0], original);
  ports[0].receive(ack(original));
  await delivery.resume();
  assert.equal(pending(state).length, 1);
  ports[1].receive(ack(original));
  await delivery.resume();
  assert.equal(pending(state).length, 0);
});

test("删除暂存失败保留确认状态，重试删除后才发送下一条", async () => {
  const { delivery, storage, ports, state } = fixture();
  await Promise.all([queue(delivery, observation(1)), queue(delivery, observation(2))]);
  const remove = storage.remove;
  storage.remove = async () => { throw new Error("remove failed"); };
  ports[0].receive(ack(ports[0].sent[0]));
  await delivery.resume();
  assert.equal(pending(state).length, 2);
  assert.equal(ports[0].sent.length, 1);
  storage.remove = remove;
  await delivery.retry();
  assert.equal(pending(state).length, 1);
  assert.equal(ports[0].sent[1].sequence, 2);
});

test("旧暂存清理失败时新事件仍然落盘，Worker 重启后可重放", async () => {
  const first = fixture();
  await queue(first.delivery, observation(1));
  first.storage.remove = async () => { throw new Error("remove failed"); };
  first.ports[0].receive(ack(first.ports[0].sent[0]));
  await first.delivery.resume();
  await queue(first.delivery, observation(2));
  assert.deepEqual(pending(first.state).map((key) => storedObservation(first.state[key]).sequence), [1, 2]);
  const restarted = fixture(first.state);
  await restarted.delivery.retry();
  restarted.ports[0].receive(ack(restarted.ports[0].sent[0]));
  await restarted.delivery.resume();
  assert.equal(restarted.ports[0].sent[1].sequence, 2);
});

test("读取下一条暂存期间断连，读取返回后等待重试", async () => {
  const { delivery, storage, ports, state } = fixture();
  await Promise.all([queue(delivery, observation(1)), queue(delivery, observation(2))]);
  const entered = Promise.withResolvers();
  const release = Promise.withResolvers();
  const get = storage.get;
  storage.get = async (key) => {
    entered.resolve();
    await release.promise;
    return get(key);
  };
  ports[0].receive(ack(ports[0].sent[0]));
  const flush = delivery.resume();
  await entered.promise;
  ports[0].disconnect();
  release.resolve();
  await flush;
  assert.equal(ports.length, 1);
  assert.equal(pending(state).length, 1);
  await delivery.retry();
  assert.equal(ports[1].sent[0].sequence, 2);
});

test("新事件写入失败时仍清理旧回执并补送已经落盘的观测", async () => {
  const { delivery, storage, ports, state } = fixture();
  await Promise.all([queue(delivery, observation(1)), queue(delivery, observation(2))]);
  const set = storage.set;
  storage.set = async () => { throw new Error("disk full"); };
  await queue(delivery, observation(3));
  ports[0].receive(ack(ports[0].sent[0]));
  await delivery.retry();
  assert.equal(pending(state).length, 1);
  assert.equal(ports[0].sent[1].sequence, 2);
  ports[0].receive(ack(ports[0].sent[1]));
  await delivery.resume();
  assert.equal(pending(state).length, 0);
  storage.set = set;
  await delivery.retry();
  assert.equal(ports[0].sent[2].sequence, 3);
});


test("持久查询写盘失败不挡 live 落盘，两种完成条件各自在真实时刻兑现", async () => {
  const { delivery, storage, ports, state } = fixture();
  const set = storage.set;
  storage.set = async (values) => {
    if (queued(values).some((key) => storedObservation(values[key]).operation === "bookmarks.getTree")) throw new Error("bulk write failed");
    await set(values);
  };
  const bulk = delivery.enqueue(observation(1, { kind: "query", operation: "bookmarks.getTree" }), "persistent");
  await delivery.resume();
  const live = delivery.enqueue(observation(2));
  await delivery.resume();
  assert.ok(pending(state).some((key) => storedObservation(state[key]).sequence === 2));
  let bulkPersisted = false;
  let liveCommitted = false;
  void bulk.persisted.then(() => { bulkPersisted = true; });
  void live.committed.then(() => { liveCommitted = true; });
  await live.persisted;
  assert.equal(bulkPersisted, false);
  assert.equal(liveCommitted, false);
  ports[0].receive(ack(ports[0].sent[0]));
  assert.deepEqual(await live.committed, { capture: `sha256-${"a".repeat(64)}` });
  storage.set = set;
  await delivery.retry();
  await bulk.persisted;
  assert.equal(bulkPersisted, true);
  assert.equal(ports[1].sent[0].sequence, 1);
  assert.equal(ports[1].sent[0].instance, ports[0].sent[0].instance);
  ports[1].receive(ack(ports[1].sent[0]));
  await bulk.committed;
});


for (const lane of ["live", "persistent"]) test(`${lane} 明确大小拒绝后按原字节分片，末片发送前回执不清除暂存`, async () => {
  const { delivery, ports, state } = fixture({}, (port, value) => {
    if (value.format !== "orisumi-browser-fragment-v1") throw new Error("Message exceeded maximum allowed size of 64MiB.");
  });
  const original = observation(7, { kind: "query", operation: "bookmarks.getTree", callbackArgs: [[{ title: "中".repeat(700000), unknown: "\n\\\"" }]] });
  const completion = delivery.enqueue(original, lane);
  await completion.persisted;
  await until(() => ports[0]?.sent.length > 0);
  const key = queued(state)[0];
  assert.equal(typeof state[key], "string");
  const first = ports[0].sent[0];
  ports[0].receive(ack(first));
  await delivery.resume();
  assert.ok(state[key]);
  await until(() => { const last = ports[0].sent.at(-1); return last.offset + Buffer.from(last.data, "base64").length === last.byteLength; });
  const parts = ports[0].sent;
  assert.ok(parts.length > 1);
  assert.ok(parts.slice(1).some((part) => (Buffer.from(part.data, "base64")[0] & 0xc0) === 0x80));
  const restored = Buffer.concat(parts.map((part) => Buffer.from(part.data, "base64")));
  assert.deepEqual(restored, Buffer.from(state[key]));
  assert.deepEqual(JSON.parse(restored), { format: "orisumi-browser-v1", instance: state.instance, ...original });
  assert.equal(first.hash, `sha256-${createHash("sha256").update(restored).digest("hex")}`);
  let offset = 0;
  for (const part of parts) {
    assert.equal(part.offset, offset);
    assert.equal(part.byteLength, restored.length);
    assert.equal(part.hash, first.hash);
    assert.ok(Buffer.byteLength(JSON.stringify(part)) <= 64 * 1024 * 1024);
    const length = Buffer.from(part.data, "base64").length;
    assert.ok(length > 0 && length <= 1024 * 1024);
    offset += length;
  }
  ports[0].receive(ack(first));
  await completion.committed;
  await delivery.resume();
  assert.equal(queued(state).length, 0);
});

test("启动只初始化一次身份，旧 persistent 固定集合确认后即可开始新一轮", async () => {
  const first = fixture();
  const set = first.storage.set;
  let identityWrites = 0;
  first.storage.set = async (values) => {
    if (Object.hasOwn(values, "instance")) identityWrites++;
    await set(values);
  };
  const live = first.delivery.enqueue(observation(1));
  const bulk = first.delivery.enqueue(observation(2, { kind: "query", operation: "history.search" }), "persistent");
  await first.delivery.resume();
  await Promise.all([live.persisted, bulk.persisted]);
  assert.equal(identityWrites, 1);
  assert.equal(first.ports.length, 2);
  assert.equal(first.ports[0].sent[0].instance, first.ports[1].sent[0].instance);
  const restarted = fixture(first.state);
  let ready = false;
  const replay = restarted.delivery.replayed("persistent").then(() => { ready = true; });
  const fresh = restarted.delivery.enqueue(observation(3, { kind: "callback", operation: "history.onVisited" }), "persistent");
  await restarted.delivery.resume();
  await fresh.persisted;
  assert.equal(ready, false);
  const persistent = restarted.ports.find((port) => port.sent[0].sequence === 2);
  const livePort = restarted.ports.find((port) => port.sent[0].sequence === 1);
  persistent.receive(ack(livePort.sent[0]));
  await restarted.delivery.resume();
  assert.equal(ready, false);
  persistent.receive(ack(persistent.sent[0]));
  await replay;
  await restarted.delivery.resume();
  assert.equal(ready, true);
  assert.equal(persistent.sent[1].sequence, 3);
  assert.ok(pending(restarted.state).some((key) => storedObservation(restarted.state[key]).sequence === 1));
});

test("persistent 分片中断保留完整源，重建从零重发且 live 可独立确认", async () => {
  const rejectLarge = (port, value) => {
    if (value.operation === "bookmarks.getTree") throw new Error("Message exceeded maximum allowed size of 64MiB.");
  };
  const first = fixture({}, rejectLarge);
  const saved = first.delivery.enqueue(observation(3, { kind: "query", operation: "bookmarks.getTree", callbackArgs: [["x".repeat(2 * 1024 * 1024)]] }), "persistent");
  await saved.persisted;
  await until(() => first.ports[0]?.sent.length > 0);
  const original = first.ports[0].sent[0];
  first.ports[0].disconnect();
  const live = first.delivery.enqueue(observation(4));
  await first.delivery.resume();
  const livePort = first.ports.find((port) => port.sent[0]?.sequence === 4);
  livePort.receive(ack(livePort.sent[0]));
  await live.committed;
  await first.delivery.resume();
  const fixedText = first.state[queued(first.state)[0]];
  const restarted = fixture(first.state, rejectLarge);
  const replay = restarted.delivery.replayed("persistent");
  await until(() => restarted.ports[0]?.sent.length > 0);
  assert.deepEqual(restarted.ports[0].sent[0], original);
  first.ports[0].receive(ack(original));
  await first.delivery.resume();
  assert.equal(queued(restarted.state).length, 1);
  await until(() => { const last = restarted.ports[0].sent.at(-1); return last.offset + Buffer.from(last.data, "base64").length === last.byteLength; });
  assert.equal(restarted.state[queued(restarted.state)[0]], fixedText);
  restarted.ports[0].receive(ack(original));
  await replay;
  await restarted.delivery.resume();
  assert.equal(queued(restarted.state).length, 0);
});

test("实际 UTF-8 恰好 64 MiB 走普通帧，超出一字节才预先分片", async () => {
  const instance = "10203040-5060-4070-8090-a0b0c0d0e0f0";
  const limit = 64 * 1024 * 1024;
  for (const extra of [0, 1]) {
    const source = observation(50 + extra, { kind: "query", operation: "bookmarks.getTree", unknown: "中文\n\\\"", payload: "" });
    const emptyBytes = Buffer.byteLength(JSON.stringify({ format: "orisumi-browser-v1", instance, ...source }));
    source.payload = "x".repeat(limit + extra - emptyBytes);
    let bytes = 0;
    let frames = 0;
    const hash = createHash("sha256");
    const current = fixture({ instance }, (port, value) => {
      if (!extra) {
        assert.equal(value.format, "orisumi-browser-v1");
        assert.equal(Buffer.byteLength(JSON.stringify(value)), limit);
      } else {
        assert.equal(value.format, "orisumi-browser-fragment-v1");
        const data = Buffer.from(value.data, "base64");
        assert.equal(value.offset, bytes);
        assert.equal(value.byteLength, limit + 1);
        assert.ok(Buffer.byteLength(JSON.stringify(value)) <= limit);
        bytes += data.length;
        frames++;
        hash.update(data);
        port.sent.length = 0;
      }
    });
    const completion = current.delivery.enqueue(source, "persistent");
    await completion.persisted;
    await until(() => extra ? bytes === limit + 1 : current.ports[0]?.sent.length === 1);
    const last = current.ports[0].sent.at(-1);
    if (extra) {
      assert.equal(frames, 65);
      assert.equal(last.hash, `sha256-${hash.digest("hex")}`);
      assert.equal(last.hash, `sha256-${createHash("sha256").update(current.state[queued(current.state)[0]]).digest("hex")}`);
    }
    current.ports[0].receive(ack(last));
    await completion.committed;
    await current.delivery.resume();
    assert.equal(queued(current.state).length, 0);
  }
});


test("旧 pending 对象原样重放，新 live 使用固定字符串与相同身份", async () => {
  const instance = "10203040-5060-4070-8090-a0b0c0d0e0f0";
  const old = { format: "orisumi-browser-v1", instance, ...observation(1) };
  const key = `pending:${run}:${String(1).padStart(16, "0")}`;
  const current = fixture({ instance, [key]: old });
  const next = current.delivery.enqueue(observation(2));
  await next.persisted;
  await current.delivery.resume();
  assert.deepEqual(current.ports[0].sent[0], old);
  assert.deepEqual(current.state[key], old);
  const freshKey = pending(current.state).find((candidate) => candidate !== key);
  assert.equal(typeof current.state[freshKey], "string");
  current.ports[0].receive(ack(old));
  await current.delivery.resume();
  assert.equal(current.ports[0].sent[1].instance, instance);
  current.ports[0].receive(ack(current.ports[0].sent[1]));
  await next.committed;
  await current.delivery.resume();
  assert.equal(pending(current.state).length, 0);
});

test("本轮退出边界收到新观测时继续落盘，不等待下一次外部重试", async () => {
  for (let depth = 0; depth < 8; depth++) {
    let second;
    const current = fixture({}, () => {
      let remaining = depth;
      const enqueue = () => {
        if (remaining--) queueMicrotask(enqueue);
        else second = current.delivery.enqueue(observation(2));
      };
      queueMicrotask(enqueue);
    });
    await current.delivery.enqueue(observation(1)).persisted;
    await new Promise(setImmediate);
    assert.ok(second);
    assert.equal(pending(current.state).length, 2, `入队时机 ${depth}`);
    await second.persisted;
  }
});
