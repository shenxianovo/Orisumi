import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test, { before } from "node:test";
import { observeBookmarks } from "../../ChromeExtension/bookmarks.js";

const root = fileURLToPath(new URL("../../../../../", import.meta.url));
const project = join(root, "src/Orisumi.Cli");
const executable = join(project, "bin/Debug/net11.0/Orisumi");
before(() => execFileSync("dotnet", ["build", project, "--nologo", "-v", "quiet"], { cwd: root }));

function database(t) {
  const store = mkdtempSync(join(tmpdir(), "orisumi-bookmarks-"));
  t.after(() => rmSync(store, { recursive: true, force: true }));
  const profileId = randomUUID();
  const runId = randomUUID();
  let sequence = 0;
  function observation(change) {
    sequence++;
    return { observationId: randomUUID(), runId, runStartedAtUtcMs: 1000,
      runSequence: sequence, observedAtUtcMs: 1000 + sequence, ...change };
  }
  function send(value, profile = profileId) {
    const payload = Buffer.from(JSON.stringify({ profileId: profile, bookmarkObservation: value }));
    const header = Buffer.alloc(4);
    header.writeUInt32LE(payload.length);
    const result = spawnSync(executable, ["--store", store, "browser", "native-host"], { input: Buffer.concat([header, payload]) });
    assert.equal(result.status, 0, result.stderr?.toString());
    assert.ok(result.stdout.length >= 4, result.stderr?.toString());
    const reply = JSON.parse(result.stdout.subarray(4).toString());
    return reply;
  }
  const sql = query => JSON.parse(execFileSync("sqlite3", ["-json", join(store, "raw/browser.sqlite"), query], { encoding: "utf8" }) || "[]");
  function write(change) {
    const value = observation(change);
    assert.equal(send(value).ok, true, JSON.stringify(value));
    return value;
  }
  return { send, write, observation, sql, profileId };
}

const node = (id, parentId, position, title = id, url = `https://example.com/${id}`) => ({
  bookmarkId: id, parentId, position, title, url, addedAtUtcMs: 900, lastUsedAtUtcMs: null,
});

test("create, rename, move and delete append independent facts and reconstruct history", t => {
  const db = database(t);
  db.write({ kind: "created", node: node("B", "work", 0, "Orisumi") });
  const rename = db.write({ kind: "changed", bookmarkId: "B", title: "Orisumi project", url: "https://example.com/B" });
  db.write({ kind: "moved", bookmarkId: "B", parentId: "reading", position: 2 });
  db.write({ kind: "removed", bookmarkId: "B" });
  assert.deepEqual(db.sql("SELECT title,parent_id,position,present FROM bookmark_history ORDER BY observation_id"), [
    { title: "Orisumi", parent_id: "work", position: 0, present: 1 },
    { title: "Orisumi project", parent_id: "work", position: 0, present: 1 },
    { title: "Orisumi project", parent_id: "reading", position: 2, present: 1 },
    { title: "Orisumi project", parent_id: "reading", position: 2, present: 0 },
  ]);
  assert.deepEqual(db.sql("SELECT * FROM current_bookmarks"), []);
  for (const [table, count] of Object.entries({ bookmarks: 1, bookmark_observations: 4,
    bookmark_presence_events: 2, bookmark_title_events: 2, bookmark_url_events: 1,
    bookmark_location_events: 2, bookmark_last_used_observations: 0 })) {
    assert.equal(db.sql(`SELECT count(*) AS n FROM ${table}`)[0].n, count, table);
  }
  assert.deepEqual(db.send(rename), { ok: true, inserted: 0, databaseId: null });
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmark_observations")[0].n, 4);
});

test("moves, reordering and subtree removal preserve the entire tree's positions", t => {
  const db = database(t);
  db.write({ kind: "snapshot", nodes: [node("0", null, null, "root", null),
    node("F1", "0", 0, "work", null), node("F2", "0", 1, "reading", null),
    node("A", "F1", 0), node("B", "F1", 1), node("C", "F1", 2), node("D", "F2", 0)] });
  db.write({ kind: "moved", bookmarkId: "B", parentId: "F2", position: 0 });
  assert.deepEqual(db.sql("SELECT bookmark_id,parent_id,position FROM current_bookmarks WHERE node_kind='bookmark' ORDER BY bookmark_id"), [
    { bookmark_id: "A", parent_id: "F1", position: 0 }, { bookmark_id: "B", parent_id: "F2", position: 0 },
    { bookmark_id: "C", parent_id: "F1", position: 1 }, { bookmark_id: "D", parent_id: "F2", position: 1 },
  ]);
  db.write({ kind: "reordered", bookmarkId: "F2", childIds: ["D", "B"] });
  db.write({ kind: "removed", bookmarkId: "F2" });
  assert.deepEqual(db.sql("SELECT bookmark_id FROM current_bookmarks ORDER BY bookmark_id"), ["0", "A", "C", "F1"].map(bookmark_id => ({ bookmark_id })));
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmarks")[0].n, 7);
  assert.equal(db.sql("SELECT position FROM bookmark_state WHERE bookmark_id='B'")[0].position, 1);
});

test("unchanged snapshots add no facts; title return and last use remain distinct changes", t => {
  const db = database(t);
  const initial = node("B", "F", 0, "first");
  db.write({ kind: "snapshot", nodes: [initial] });
  db.write({ kind: "snapshot", nodes: [initial] });
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmark_observations")[0].n, 1);
  db.write({ kind: "changed", bookmarkId: "B", title: "second" });
  db.write({ kind: "changed", bookmarkId: "B", title: "first" });
  db.write({ kind: "snapshot", nodes: [{ ...initial, lastUsedAtUtcMs: 1004 }] });
  assert.deepEqual(db.sql("SELECT title FROM bookmark_title_events ORDER BY observation_id"), ["first", "second", "first"].map(title => ({ title })));
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmark_url_events")[0].n, 1);
  assert.equal(db.sql("SELECT last_used_at_utc_ms FROM current_bookmarks")[0].last_used_at_utc_ms, 1004);
  const count = db.sql("SELECT count(*) AS n FROM bookmark_observations")[0].n;
  db.write({ kind: "snapshot", nodes: [{ ...initial, addedAtUtcMs: null }] });
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmark_observations")[0].n, count);
  assert.equal(db.sql("SELECT last_used_at_utc_ms FROM current_bookmarks")[0].last_used_at_utc_ms, 1004);
  db.write({ kind: "snapshot", nodes: [] });
  assert.equal(db.sql("SELECT present FROM bookmark_state")[0].present, 0);
});

test("invalid observations roll back and equal bookmark IDs in different profiles stay separate", t => {
  const db = database(t);
  db.write({ kind: "created", node: node("B", "F", 0) });
  assert.equal(db.send(db.observation({ kind: "reordered", bookmarkId: "F", childIds: ["B", "B"] })).ok, false);
  const second = db.observation({ kind: "created", runId: randomUUID(), node: node("B", "G", 0, "other") });
  assert.equal(db.send(second, randomUUID()).ok, true);
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmarks")[0].n, 2);
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmark_observations")[0].n, 2);
});

function browser() {
  const local = {};
  const session = {};
  const listeners = {};
  const saved = [];
  const storage = data => ({
    get: async key => ({ [key]: structuredClone(data[key]) }),
    set: async values => Object.assign(data, structuredClone(values)),
  });
  globalThis.chrome = {
    storage: { local: storage(local), session: storage(session) },
    bookmarks: {
      getTree: async () => [],
      ...Object.fromEntries(["onCreated", "onChanged", "onMoved", "onRemoved", "onChildrenReordered"].map(name => [name, { addListener: callback => { listeners[name] = callback; } }])),
    },
    runtime: { sendNativeMessage: async (_host, batch) => { saved.push(batch.bookmarkObservation); return { ok: true, inserted: 1 }; } },
  };
  return { local, session, saved, emit: (name, ...args) => listeners[name](...args) };
}

test("extension keeps the same event identity through a lost acknowledgement and worker restart", async () => {
  const fixture = browser();
  let fail = true;
  chrome.runtime.sendNativeMessage = async (_host, batch) => {
    fixture.saved.push(batch.bookmarkObservation);
    if (fail) throw new Error("lost acknowledgement");
    return { ok: true, inserted: 1 };
  };
  const collector = observeBookmarks();
  fixture.emit("onCreated", "B", { id: "B", parentId: "F", index: 0, title: "first", url: "https://example.com" });
  await assert.rejects(collector.flush(), /lost acknowledgement/);
  assert.equal(fixture.local.bookmarkCaptureState.pending.length, 1);
  fail = false;
  const restarted = observeBookmarks();
  await restarted.flush();
  assert.equal(fixture.saved[0].observationId, fixture.saved.at(-1).observationId);
  assert.equal(fixture.local.bookmarkCaptureState.pending.length, 0);
  fixture.emit("onChanged", "B", { title: "second" });
  await restarted.flush();
  assert.equal(fixture.saved.at(-1).runSequence, 2);
  assert.equal(fixture.saved.at(-1).runId, fixture.saved[0].runId);
});

test("a change during a snapshot cannot overwrite that change with a stale tree", async () => {
  const fixture = browser();
  const collector = observeBookmarks();
  let reads = 0;
  chrome.bookmarks.getTree = async () => {
    reads++;
    if (reads === 1) fixture.emit("onChanged", "B", { title: "new" });
    return [{ id: "B", title: reads === 1 ? "old" : "new", url: "https://example.com" }];
  };
  await collector.sync();
  assert.equal(reads, 2);
  assert.deepEqual(fixture.saved.map(event => event.kind), ["changed", "snapshot"]);
  assert.equal(fixture.saved[1].nodes[0].title, "new");
});
