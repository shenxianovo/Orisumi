import type { ActiveTabEvent } from "./active-tabs.js";
import type { Bookmark, Download } from "./bookmarks-downloads.js";
import type { Visit } from "./history.js";

type Batch =
  | { visits: Visit[] }
  | { activeTabEvents: ActiveTabEvent[] }
  | { bookmarks: Bookmark[] }
  | { downloads: Download[] };

const host = "com.orisumi.browser";
const encoder = new TextEncoder();
export const batchBytesAvailable = 8 * 1024 * 1024 - 128;
export const jsonBytes = (value: unknown) => encoder.encode(JSON.stringify(value)).length;
let profile: Promise<string> | undefined;

async function readProfileId(): Promise<string> {
  const { profileId } = await chrome.storage.local.get<{ profileId?: string }>("profileId");
  if (profileId) return profileId;

  const id = crypto.randomUUID();
  await chrome.storage.local.set({ profileId: id });
  return id;
}

async function send(request: Batch | { getDatabaseId: true }) {
  profile ??= readProfileId().catch((error: unknown) => {
    profile = undefined;
    throw error;
  });
  const reply: { ok?: boolean; error?: string; inserted?: number; databaseId?: string } | undefined =
    await chrome.runtime.sendNativeMessage(host, { profileId: await profile, ...request });
  if (!reply?.ok) throw new Error(reply?.error ?? "The local collector did not confirm the request.");
  return reply;
}

export async function sendBatch(batch: Batch): Promise<void> {
  const { inserted } = await send(batch);
  if (typeof inserted !== "number" || !Number.isSafeInteger(inserted) || inserted < 0) {
    throw new Error("The local collector did not confirm the write.");
  }
}

export async function getDatabaseId(): Promise<string> {
  const { databaseId } = await send({ getDatabaseId: true });
  if (typeof databaseId !== "string" || !databaseId) {
    throw new Error("The local collector did not identify the database.");
  }
  return databaseId;
}
