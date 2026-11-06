import test from "node:test";
import assert from "node:assert/strict";
import { Delivery } from "./delivery.js";

const run = "3ed5f16a-88b6-4ad8-806f-741663f22da3";
const observation = (sequence, overrides = {}) => ({ run, sequence, observedAt: "2026-11-06T13:00:00.000Z", kind: "callback", operation: "tabs.onActivated", callbackArgs: [{ tabId: 17, windowId: 3 }], ...overrides });
const pending = (state) => Object.keys(state).filter((key) => key.startsWith("pending:"));
const ack = (sent) => ({ format: "orisumi-browser-ack-v1", instance: sent.instance, run: sent.run, sequence: sent.sequence, capture: `sha256-${"a".repeat(64)}` });

function fixture(state = {}) {
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
          assert.ok(pending(state).some((key) => state[key].run === value.run && state[key].sequence === value.sequence));
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
  await Promise.all([delivery.enqueue(observation(1)), delivery.enqueue(observation(2))]);
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
  await first.delivery.enqueue(observation(8));
  first.ports[0].disconnect();
  await first.delivery.enqueue(observation(9));
  assert.equal(first.ports.length, 1);
  const restarted = fixture(first.state);
  await restarted.delivery.enqueue(observation(1, { run: "f1ed7dd1-2730-43da-9844-1c37b1ee87df" }));
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
  await delivery.enqueue(observation(1));
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
  await delivery.enqueue(observation(7));
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
  await Promise.all([delivery.enqueue(observation(1)), delivery.enqueue(observation(2))]);
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
  await first.delivery.enqueue(observation(1));
  first.storage.remove = async () => { throw new Error("remove failed"); };
  first.ports[0].receive(ack(first.ports[0].sent[0]));
  await first.delivery.resume();
  await first.delivery.enqueue(observation(2));
  assert.deepEqual(pending(first.state).map((key) => first.state[key].sequence), [1, 2]);
  const restarted = fixture(first.state);
  await restarted.delivery.retry();
  restarted.ports[0].receive(ack(restarted.ports[0].sent[0]));
  await restarted.delivery.resume();
  assert.equal(restarted.ports[0].sent[1].sequence, 2);
});

test("读取下一条暂存期间断连，读取返回后等待重试", async () => {
  const { delivery, storage, ports, state } = fixture();
  await Promise.all([delivery.enqueue(observation(1)), delivery.enqueue(observation(2))]);
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
  await Promise.all([delivery.enqueue(observation(1)), delivery.enqueue(observation(2))]);
  const set = storage.set;
  storage.set = async () => { throw new Error("disk full"); };
  await delivery.enqueue(observation(3));
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
