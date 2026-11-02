import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import type { Observation, ThingActivity } from "@/api/types";
import { buildArchive, type Archive } from "@/lib/archive";
import { NAME_SCHEMA, NAMES, PERSON_SCHEMA, USER_KEY } from "@/lib/common";
import { EPISODES } from "@/lib/episodes";
import { refreshView } from "./queries";
import { observations } from "./useApplications";
import { pool, schemaOf } from "./useTypes";

/** 看到过它的 Observer 从每种经历最近的这么多份里取。 */
const RECENT = 50;

export interface ArchiveData extends Archive {
  sameAsThingIds: string[];
  activity: ThingActivity[];
  /** 每种经历最新的一份，键是 resourceName。 */
  latest: ReadonlyMap<string, Observation>;
}

/** 一个事物的档案：对象视图是现在的样子和发生过的事，再读出怎样称呼它和它所在的几件事。 */
export function useArchive(thingId: string) {
  const source = useSource();
  const client = useQueryClient();
  return useQuery({
    queryKey: ["archive", thingId],
    queryFn: async (): Promise<ArchiveData> => {
      const [view, names] = await Promise.all([
        source.getThing(thingId),
        observations(source, { thingId, resourceName: NAMES, view: "all" }),
      ]);
      const recent = await pool(view.activity, 4, async (item) => ({
        item,
        observations: (await source.readActivity(item.href, { limit: RECENT, order: "desc" })).observations,
      }));
      const statements = [...view.state, ...view.relations];
      const schemas = await pool(statements, 12, (statement) => schemaOf(client, source, statement));
      const archive = buildArchive(
        new Set([thingId, ...view.sameAsThingIds]),
        statements.map((statement, i) => ({ observation: statement, schema: schemas[i]?.content ?? null })),
      );
      const episodes = recent
        .filter(({ item }) => item.resourceName === EPISODES)
        .flatMap((entry) => entry.observations);
      const observers = new Set([
        ...statements.map((statement) => statement.observerId),
        ...names.map((name) => name.observerId),
        ...recent.flatMap((entry) => entry.observations.map((observation) => observation.observerId)),
      ]);
      observers.delete(thingId);
      return {
        ...archive,
        names,
        episodes,
        observers: [...observers],
        sameAsThingIds: view.sameAsThingIds,
        latest: new Map(
          recent.flatMap(({ item, observations: list }) => (list[0] ? [[item.resourceName, list[0]]] : [])),
        ),
        activity: view.activity.filter((item) => item.resourceName !== EPISODES),
      };
    },
    staleTime: 10_000,
    retry: false,
  });
}

/** 对象视图 activity 的一项逐页列出，新的在前。 */
export function useActivity(href: string, limit: number) {
  const source = useSource();
  return useInfiniteQuery({
    queryKey: ["activity", href],
    queryFn: ({ pageParam }) =>
      source.readActivity(href, { limit, order: "desc", ...(pageParam ? { after: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.next ?? undefined,
  });
}

/** 用户本人给事物写一个名字：一份 stated 的 Name，按 SCHEMA-20 优先使用。 */
export function useWriteName() {
  const source = useSource();
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ thingId, name }: { thingId: string; name: string }) => {
      const response = await source.write({
        observer: { schemaId: PERSON_SCHEMA, key: USER_KEY },
        writes: [{ schemaId: NAME_SCHEMA, formation: "stated", content: { thingId, name } }],
      });
      const result = response.results[0]!;
      if ("error" in result)
        throw new ApiError(result.error.status ?? 400, result.error.detail ?? "名字没有保存", result.error);
    },
    onSuccess: () => refreshView(client),
  });
}
