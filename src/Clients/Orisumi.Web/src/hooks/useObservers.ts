import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { readAllThings, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isEntitySchema, isObservation, type ObservationVersion, type Thing } from "@/api/types";
import { isUser, PROGRAM_SCHEMA } from "@/lib/common";
import { observerIdentity, type ObserverIdentity } from "@/lib/observers";
import { pool, readVersion } from "./useTypes";

export const OBSERVERS_INTERVAL = 60_000;

/** 一个 Observer：程序实例或用户本人，以及程序实例最新的描述。 */
export interface ObserverInfo {
  thing: Thing;
  description: ObservationVersion | null;
  identity: ObserverIdentity;
}

/**
 * 程序实例的描述：它最近写下的 Observation 引用的那一版描述。还只写过描述时，那份 Observation 就是描述。
 * 人没有描述。
 */
async function descriptionOf(client: QueryClient, source: OrisumiSource, thing: Thing) {
  if (thing.schemaId !== PROGRAM_SCHEMA) return null;
  const latest = (await source.listReferrers(thing.entityId, { path: "/observerId", order: "desc", limit: 1 }))
    .referrers[0]?.version;
  if (!latest || !isObservation(latest)) return null;
  if (!latest.observerVersionId) return latest;
  const description = await readVersion(client, source, latest.observerVersionId);
  return description && isObservation(description) ? description : null;
}

export async function describeObserver(
  client: QueryClient,
  source: OrisumiSource,
  thing: Thing,
): Promise<ObserverInfo> {
  const description = await descriptionOf(client, source, thing);
  const schema = description ? await readVersion(client, source, description.schemaVersionId) : null;
  const type = schema && isEntitySchema(schema) ? schema.content : null;
  return {
    thing,
    description,
    identity: isUser(thing)
      ? observerIdentity(thing.entityId, thing.name ?? "用户本人", "people")
      : observerIdentity(thing.entityId, type?.displayName ?? null, type?.resourceName ?? null),
  };
}

/** 全部 Observer：每个程序实例和用户本人。键是 EntityId。 */
export function useObservers() {
  const source = useSource();
  const client = useQueryClient();
  return useQuery({
    queryKey: ["observers"],
    queryFn: async () => {
      const [programs, people] = await Promise.all(
        ["program-instances", "people"].map((resourceName) =>
          readAllThings((after) => source.listThings({ resourceName, limit: 1000, ...(after ? { after } : {}) })),
        ),
      );
      const things = [...programs!, ...people!.filter(isUser)];
      const infos = await pool(things, 6, (thing) => describeObserver(client, source, thing));
      return new Map(infos.map((info) => [info.thing.entityId, info]));
    },
    refetchInterval: OBSERVERS_INTERVAL,
  });
}
