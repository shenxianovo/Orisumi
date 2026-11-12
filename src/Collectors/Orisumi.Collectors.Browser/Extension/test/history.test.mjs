import assert from "node:assert/strict";
import test from "node:test";
import { syncHistory } from "../../ChromeExtension/history.js";

test("history retries an unacknowledged range and replays it after database replacement", async () => {
  const visitTime = Date.now() - 2 * 3_600_000;
  const url = "https://example.com/";
  const visits = Array.from({ length: 1001 }, (_, index) => ({ visitId: String(index), visitTime }));
  const storage = {};
  let databaseId = "original";
  let failLastBatch = true;
  const saved = new Set();
  const attempts = [];

  globalThis.chrome = {
    history: {
      search: async ({ startTime, endTime }) =>
        startTime <= visitTime && visitTime < endTime ? [{ url, lastVisitTime: visitTime }] : [],
      getVisits: async () => visits,
    },
    runtime: {
      async sendNativeMessage(_host, request) {
        if (request.getDatabaseId) return { ok: true, databaseId };
        const batch = request.visits;
        attempts.push(batch.map(visit => visit.visitId));
        for (const visit of batch) saved.add(visit.visitId);
        if (batch.length === 1 && failLastBatch) {
          failLastBatch = false;
          throw new Error("lost acknowledgement");
        }
        return { ok: true, inserted: batch.length };
      },
    },
    storage: {
      local: {
        get: async key => ({ [key]: structuredClone(storage[key]) }),
        set: async values => Object.assign(storage, structuredClone(values)),
      },
    },
  };

  await assert.rejects(syncHistory(), /lost acknowledgement/);
  assert.equal(saved.size, 1001);
  assert.ok(storage.visitSyncState.backfillBefore > visitTime);

  await syncHistory();
  assert.deepEqual(attempts.slice(2), attempts.slice(0, 2));
  assert.equal(storage.visitSyncState.backfillBefore, 0);

  await syncHistory();
  assert.equal(attempts.length, 4);
  databaseId = "replacement";
  saved.clear();

  await syncHistory();
  assert.equal(saved.size, 1001);
  assert.equal(storage.visitSyncState.databaseId, databaseId);
});
