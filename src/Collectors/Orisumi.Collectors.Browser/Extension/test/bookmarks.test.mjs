import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { execFileSync, spawn, spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
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
  function sendBatch(batch, profile = profileId) {
    const payload = Buffer.from(JSON.stringify({ profileId: profile, ...batch }));
    const header = Buffer.alloc(4);
    header.writeUInt32LE(payload.length);
    const result = spawnSync(executable, ["--store", store, "browser", "native-host"], { input: Buffer.concat([header, payload]) });
    assert.equal(result.status, 0, result.stderr?.toString());
    assert.ok(result.stdout.length >= 4, result.stderr?.toString());
    const reply = JSON.parse(result.stdout.subarray(4).toString());
    return reply;
  }
  const send = (value, profile = profileId) => sendBatch({ bookmarkObservation: value }, profile);
  // The database the receiver writes now, as the store's pointer names it (storage.mdx).
  const file = () => join(store, "observations/browser",
    JSON.parse(readFileSync(join(store, "state/current/browser.json"), "utf8")).databaseId + ".sqlite");
  const sql = query => JSON.parse(execFileSync("sqlite3", ["-json", file(), query], { encoding: "utf8" }) || "[]");
  function write(change) {
    const value = observation(change);
    assert.equal(send(value).ok, true, JSON.stringify(value));
    return value;
  }
  return { send, sendBatch, write, observation, sql, file, profileId, store };
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
  // B plus the two parents it was placed under.
  for (const [table, count] of Object.entries({ bookmarks: 3, bookmark_observations: 4,
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
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmarks WHERE bookmark_id='B'")[0].n, 2);
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmark_observations")[0].n, 2);
});

test("a later snapshot writes only the changed last use and records missed events", t => {
  const db = database(t);
  db.write({ kind: "snapshot", nodes: [node("B", "F", 0)] });
  db.write({ kind: "snapshot", nodes: [{ ...node("B", "F", 0), lastUsedAtUtcMs: 2000 }] });
  assert.deepEqual(db.sql("SELECT last_used_at_utc_ms FROM bookmark_last_used_observations"), [{ last_used_at_utc_ms: 2000 }]);
  for (const table of ["bookmark_presence_events", "bookmark_title_events", "bookmark_url_events", "bookmark_location_events"])
    assert.equal(db.sql(`SELECT count(*) AS n FROM ${table}`)[0].n, 1, table);
  assert.equal(db.sql("SELECT count(*) AS n FROM capture_channel_events")[0].n, 0);
  // A rename that arrived without its event is found by the next snapshot.
  db.write({ kind: "snapshot", nodes: [{ ...node("B", "F", 0, "renamed"), lastUsedAtUtcMs: 2000 }] });
  assert.deepEqual(db.sql("SELECT evidence FROM capture_channel_events"), [{ evidence: "reconciliation_changed" }]);
});

test("coverage retries are idempotent and an unconfirmed run remains open across restarts", t => {
  const db = database(t), first = randomUUID(), second = randomUUID();
  const event = (runId, startedAtUtcMs, evidence, stoppedAtUtcMs = null) => ({
    eventId: randomUUID(), scope: "bookmarks", mode: "notification", startedAtUtcMs, stoppedAtUtcMs, evidence, detail: null, runId,
  });
  const entries = [event(first, 1000, "available"), event(first, 2000, "unavailable"), event(first, 3000, "available"),
    event(second, 4000, "available"), { ...event(second, 4100, "sampled", 4200), scope: "bookmarks.last_used", mode: "snapshot" }];
  assert.equal(db.sendBatch({ coverage: entries }).ok, true);
  assert.equal(db.sendBatch({ coverage: entries }).ok, true);
  assert.deepEqual(db.sql("SELECT started_at_utc_ms,stopped_at_utc_ms FROM capture_coverage ORDER BY started_at_utc_ms"), [
    { started_at_utc_ms: 1000, stopped_at_utc_ms: 2000 }, { started_at_utc_ms: 2000, stopped_at_utc_ms: 3000 },
    { started_at_utc_ms: 3000, stopped_at_utc_ms: null }, { started_at_utc_ms: 4000, stopped_at_utc_ms: null },
    { started_at_utc_ms: 4100, stopped_at_utc_ms: 4200 },
  ]);
});

test("unknown tab fields clear prior values and download baselines survive a new run", t => {
  const db = database(t), run = randomUUID();
  const tab = (sequence, title, url) => ({ eventId: randomUUID(), captureSessionId: run, runStartedAtUtcMs: 1000,
    sessionSequence: sequence, observedAtUtcMs: 1000 + sequence, eventKind: sequence === 1 ? "snapshot" : "tab_updated",
    tabs: [{ tabId: 1, fields: sequence === 1 ? { isPresent: true, windowId: 2, title, url } : { title, url } }],
    ...(sequence === 1 ? { windows: [{ windowId: 2, isPresent: true }], focus: { tabId: null } } : {}) });
  assert.equal(db.sendBatch({ tabEvents: [tab(1, "A", "https://example.com"), tab(2, null, null)] }).ok, true);
  assert.deepEqual(db.sql("SELECT title,url FROM tab_history ORDER BY run_sequence"), [
    { title: "A", url: "https://example.com" }, { title: null, url: null },
  ]);
  const download = runId => ({ eventId: randomUUID(), runId, runStartedAtUtcMs: 1000, runSequence: 1,
    observedAtUtcMs: 2000, kind: "snapshot", downloadId: 1, fields: { state: "complete", filename: "/tmp/a", url: "https://example.com/file" } });
  assert.equal(db.sendBatch({ downloads: [download(randomUUID()), download(randomUUID())] }).ok, true);
  for (const table of ["download_state_observations", "download_path_observations"])
    assert.equal(db.sql(`SELECT count(*) AS n FROM ${table}`)[0].n, 2, table);
  assert.deepEqual(db.sql("SELECT url FROM downloads"), [{ url: "https://example.com/file" }]);
  assert.equal(db.sql("SELECT count(*) AS n FROM download_response_observations")[0].n, 0);
});

test("known absence is explicit: root location, tab focus and download end time", t => {
  const db = database(t);
  db.write({ kind: "snapshot", nodes: [node("0", null, null, "", null), node("F", "0", 0, "folder", null), node("B", "F", 0)] });
  assert.deepEqual(db.sql("SELECT bookmark_id,node_kind,parent_id,position FROM current_bookmarks ORDER BY bookmark_id"), [
    { bookmark_id: "0", node_kind: "root", parent_id: null, position: null },
    { bookmark_id: "B", node_kind: "bookmark", parent_id: "F", position: 0 },
    { bookmark_id: "F", node_kind: "folder", parent_id: "0", position: 0 },
  ]);
  assert.equal(db.sql("SELECT count(*) AS n FROM bookmark_location_events")[0].n, 2);

  const run = randomUUID();
  const focus = (sequence, eventKind, tabId) => ({ eventId: randomUUID(), captureSessionId: run, runStartedAtUtcMs: 1000,
    sessionSequence: sequence, observedAtUtcMs: 1000 + sequence, eventKind, focus: { tabId },
    ...(sequence === 1 ? { windows: [{ windowId: 2, isPresent: true }],
      tabs: [{ tabId, fields: { isPresent: true, windowId: 2, title: "A", url: "https://example.com" } }] } : {}) });
  assert.equal(db.sendBatch({ tabEvents: [focus(1, "snapshot", 1), focus(2, "window_focus_changed", null)] }).ok, true);
  assert.deepEqual(db.sql("SELECT has_tab,tab_id FROM active_tab_focus_events LEFT JOIN tabs USING(tab_key) ORDER BY observation_id"), [
    { has_tab: 1, tab_id: 1 }, { has_tab: 0, tab_id: null },
  ]);

  const downloadRun = randomUUID();
  const download = (sequence, kind, fields) => ({ eventId: randomUUID(), runId: downloadRun, runStartedAtUtcMs: 1000,
    runSequence: sequence, observedAtUtcMs: 2000 + sequence, kind, downloadId: 7, fields });
  assert.equal(db.sendBatch({ downloads: [
    download(1, "snapshot", { state: "interrupted", endedAtUtcMs: 2500 }),
    download(2, "changed", { state: "in_progress" }),
  ] }).ok, true);
  assert.deepEqual(db.sql("SELECT state,ended_at_utc_ms FROM download_state_observations ORDER BY observation_id"), [
    { state: "interrupted", ended_at_utc_ms: 2500 }, { state: "in_progress", ended_at_utc_ms: null },
  ]);
});

test("delayed title callbacks survive newer samples and retry with the same identity", t => {
  const db = database(t), url = "https://example.com";
  const page = (title, observedAtUtcMs, eventId) => ({ url, title, observedAtUtcMs, eventId });
  assert.equal(db.sendBatch({ historyPages: [page("A", 1000)] }).ok, true);
  const delayed = page("B", 2000, randomUUID());
  assert.equal(db.sendBatch({ historyPages: [page("C", 3000)] }).ok, true);
  assert.equal(db.sendBatch({ historyPages: [delayed] }).ok, true);
  assert.equal(db.sendBatch({ historyPages: [delayed] }).ok, true);
  assert.deepEqual(db.sql("SELECT title FROM history_page_title_observations ORDER BY observed_at_utc_ms"), ["A", "B", "C"].map(title => ({ title })));
  assert.equal(db.sql("SELECT title FROM history_pages")[0].title, "C");
  assert.equal(db.sendBatch({ historyPages: [page("A", 4000, randomUUID()), page("B", 4000, randomUUID()), page("A", 4000, randomUUID())] }).ok, true);
  assert.deepEqual(db.sql("SELECT title FROM history_page_title_observations WHERE observed_at_utc_ms=4000 ORDER BY observation_id"), ["A", "B", "A"].map(title => ({ title })));
});

test("native host waits for a concurrent SQLite writer without reopening the database as a file", async t => {
  const db = database(t);
  assert.equal(db.sendBatch({ getDatabaseId: true }).ok, true);
  const writer = spawn("sqlite3", [db.file()]);
  const exited = new Promise(resolve => writer.on("exit", resolve));
  t.after(() => writer.kill());
  const locked = new Promise(resolve => writer.stdout.once("data", resolve));
  writer.stdin.write("BEGIN EXCLUSIVE;\nSELECT 'locked';\n");
  await locked;
  const payload = Buffer.from(JSON.stringify({ profileId: db.profileId,
    visits: [{ visitId: "concurrent", visitedAtUtcMs: 1000, url: "https://example.com" }] }));
  const header = Buffer.alloc(4); header.writeUInt32LE(payload.length);
  const receiver = spawn(executable, ["--store", db.store, "browser", "native-host"]);
  const output = [], errors = [];
  receiver.stdout.on("data", value => output.push(value));
  receiver.stderr.on("data", value => errors.push(value));
  const replied = new Promise(resolve => receiver.on("exit", () => resolve(JSON.parse(Buffer.concat(output).subarray(4)))));
  receiver.stdin.end(Buffer.concat([header, payload]));
  setTimeout(() => writer.stdin.end("COMMIT;\n.quit\n"), 1000);
  const reply = await replied;
  await exited;
  assert.equal(reply.ok, true, Buffer.concat(errors).toString());
  assert.equal(db.sql("SELECT count(*) AS n FROM visits")[0].n, 1);
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
