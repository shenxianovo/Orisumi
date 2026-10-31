import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import { isObservation, type ObservationVersion } from "@/api/types";
import { buildArchive, type Archive } from "@/lib/archive";
import { NAME_SCHEMA, NAMES, PERSON_SCHEMA, RELATIONS, USER_KEY } from "@/lib/common";
import { EPISODES } from "@/lib/episodes";
import { observations } from "./useApplications";
import { pool, schemaOf, useTypes } from "./useTypes";

/** 一次读取的引用方数。设备和 Observer 的引用方可以有几万份，只读最近的这些。 */
const RECENT = 1000;

export interface ArchiveData extends Archive {
  /** 更早的引用方没有读取。 */
  truncated: boolean;
}

/** 一个事物的档案：名字和关系总是读全，其余读最近的引用方，再按 EntitySchema 分组。 */
export function useArchive(thingId: string) {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  return useQuery({
    queryKey: ["archive", thingId],
    queryFn: async (): Promise<ArchiveData> => {
      const pick = (resourceName: string) => source.listReferrers(thingId, { resourceName, limit: 1000 });
      const [names, relations, recent, current] = await Promise.all([
        pick(NAMES),
        pick(RELATIONS),
        source.listReferrers(thingId, { order: "desc", limit: RECENT }),
        observations(source, { resourceName: EPISODES, standing: "current" }),
      ]);
      const seen = new Set<string>();
      const versions: ObservationVersion[] = [];
      for (const { version } of [...names.referrers, ...relations.referrers, ...recent.referrers]) {
        if (!isObservation(version) || seen.has(version.versionId)) continue;
        seen.add(version.versionId);
        versions.push(version);
      }
      versions.sort((a, b) => b.versionId.localeCompare(a.versionId));
      const schemas = await pool(versions, 12, (version) => schemaOf(client, source, types.data!, version));
      const archive = buildArchive(
        thingId,
        versions.map((version, i) => ({ version, schema: schemas[i]?.content ?? null })),
      );
      const standing = new Set(current.map((version) => version.versionId));
      return {
        ...archive,
        episodes: archive.episodes.filter((version) => standing.has(version.versionId)),
        truncated: recent.next !== null,
      };
    },
    enabled: !!types.data,
    staleTime: 30_000,
  });
}

/** 用户本人给事物写一个名字：一份亲口陈述的 Name，按 UNDERSTANDING-9 优先使用。 */
export function useWriteName() {
  const source = useSource();
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ thingId, name }: { thingId: string; name: string }) => {
      const response = await source.write({
        observer: { schemaId: PERSON_SCHEMA, key: USER_KEY },
        writes: [{ schemaId: NAME_SCHEMA, formation: "stated", content: { thingId, name } }],
      });
      const result = response.results[0];
      if (result && "error" in result)
        throw new ApiError(result.error.status ?? 400, result.error.detail ?? "名字没有保存", result.error);
    },
    onSuccess: () => client.invalidateQueries(),
  });
}
