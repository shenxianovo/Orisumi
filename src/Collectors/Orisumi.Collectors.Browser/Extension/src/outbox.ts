export type Scope = "tabs" | "downloads" | "bookmarks" | "history";
type Delivery = { scope: Scope; kind: string; data: unknown; observedAtUtcMs: number; browserSession: string };

export function openOutbox() {
  return new Promise<IDBDatabase>((resolve, reject) => {
    const open = indexedDB.open("orisumi-outbox", 1);
    open.onupgradeneeded = () => {
      open.result.createObjectStore("outbox", { keyPath: "sequence", autoIncrement: true });
      open.result.createObjectStore("identity").add(crypto.randomUUID(), "queueId");
    };
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(open.error);
  }).then(db => {
    function transact<T>(store: string, mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>) {
      return new Promise<T>((resolve, reject) => {
        const transaction = db.transaction(store, mode, { durability: "strict" });
        const request = action(transaction.objectStore(store));
        transaction.oncomplete = () => resolve(request.result);
        transaction.onabort = () => reject(transaction.error);
      });
    }
    return {
      queueId: () => transact<string>("identity", "readonly", store => store.get("queueId")),
      first: async () => (await transact<IDBCursorWithValue | null>("outbox", "readonly", store => store.openCursor()))
        ?.value as (Delivery & { sequence: number }) | undefined,
      add: (delivery: Delivery) => transact("outbox", "readwrite", store => store.add(delivery)),
      remove: (sequence: number) => transact("outbox", "readwrite", store => store.delete(sequence)),
    };
  });
}
