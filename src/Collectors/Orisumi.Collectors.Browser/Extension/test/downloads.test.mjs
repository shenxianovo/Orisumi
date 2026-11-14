import assert from "node:assert/strict";
import test from "node:test";
import { observeDownloads } from "../../ChromeExtension/downloads.js";

test("download callbacks preserve intermediate states and retry the same durable event", async () => {
  const local = {}, session = {}, listeners = {}, sent = [];
  let loseAck = true;
  const storage = data => ({ get: async key => ({ [key]: structuredClone(data[key]) }), set: async values => Object.assign(data, structuredClone(values)) });
  const event = name => ({ addListener: callback => { listeners[name] = callback; } });
  const item = { id: 1, url: "https://example.com/file", finalUrl: "https://example.com/file", startTime: "2026-01-01T00:00:00Z",
    filename: "/tmp/file", state: "complete", totalBytes: 10, mime: "text/plain", incognito: false };
  globalThis.chrome = { storage: { local: storage(local), session: storage(session) },
    downloads: { onCreated: event("created"), onChanged: event("changed"), onErased: event("erased"), search: async () => [item] },
    runtime: { sendNativeMessage: async (_host, batch) => { sent.push(structuredClone(batch.downloads));
      if (loseAck) { loseAck = false; throw new Error("lost acknowledgement"); } return { ok: true, inserted: 1 }; } } };
  const collector = observeDownloads();
  listeners.created({ ...item, state: "in_progress" });
  listeners.changed({ id: 1, state: { previous: "in_progress", current: "interrupted" } });
  listeners.changed({ id: 1, state: { previous: "interrupted", current: "complete" } });
  await assert.rejects(collector.sync(), /lost acknowledgement/);
  await collector.sync();
  const facts = [...new Map(sent.flat().map(event => [event.eventId, event])).values()];
  assert.deepEqual(facts.slice(0, 3).map(event => event.fields.state), ["in_progress", "interrupted", "complete"]);
  assert.equal(sent[0][0].eventId, sent[1][0].eventId);
  assert.equal(local.downloadFacts.pending.length, 0);
  assert.ok(facts.every((event, i) => event.runSequence === i + 1));
});

test("a known ordinary download keeps callback values after its list item disappears", async () => {
  const local = {}, session = {}, listeners = {}, sent = [];
  const storage = data => ({ get: async key => ({ [key]: structuredClone(data[key]) }), set: async values => Object.assign(data, structuredClone(values)) });
  const event = name => ({ addListener: callback => { listeners[name] = callback; } });
  const item = { id: 9, url: "https://example.com/file", startTime: "2026-01-01T00:00:00Z",
    filename: "/tmp/file", state: "in_progress", totalBytes: 10, mime: "text/plain", incognito: false };
  let visible = true;
  globalThis.chrome = { storage: { local: storage(local), session: storage(session) },
    downloads: { onCreated: event("created"), onChanged: event("changed"), onErased: event("erased"), search: async () => visible ? [item] : [] },
    runtime: { sendNativeMessage: async (_host, batch) => { if(batch.downloads)sent.push(...batch.downloads); return { ok: true, inserted: 1 }; } } };
  const collector = observeDownloads();
  listeners.created(item);
  await collector.sync();
  visible = false;
  listeners.changed({ id: 9, state: { previous: "in_progress", current: "complete" } });
  await collector.sync();
  assert.equal(sent.at(-1).fields.state, "complete");
});
