import { useSyncExternalStore } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import type { EntitySchema, ObservationVersion } from "@/api/types";
import { PERSON_SCHEMA, REVIEWS, type ReviewContent } from "@/lib/common";
import { EPISODES } from "@/lib/episodes";
import { readSeen, subscribeSeen } from "@/lib/preferences";
import { MINUTE } from "@/lib/time";
import { observations } from "./useApplications";
import { pool, readThings, schemaOf, useTypes } from "./useTypes";

/** 一条模型的判断。review 是用户本人对它的复核，用来写“已确认”“已否定”和撤回。 */
export interface Judgment {
  version: ObservationVersion;
  schema: EntitySchema | null;
  review: ObservationVersion | null;
  verdict: "confirm" | "deny" | null;
}

const JUDGMENTS_INTERVAL = MINUTE;

/** 模型形成的判断：推断的 Observation，几件事除外。被取代的不列出；被用户否定的留着，可以撤回。 */
export function useJudgments() {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  return useQuery({
    queryKey: ["judgments"],
    queryFn: async (): Promise<Judgment[]> => {
      const [inferred, reviews] = await Promise.all([
        observations(source, { formation: "inferred" }),
        observations(source, { resourceName: REVIEWS }),
      ]);
      const schemas = await pool(inferred, 12, (version) => schemaOf(client, source, types.data!, version));
      const reviewers = await readThings(
        client,
        source,
        reviews.map((review) => review.observerId),
      );
      const byTarget = new Map<string, ObservationVersion[]>();
      for (const review of reviews) {
        const { targetVersionId } = review.content as unknown as ReviewContent;
        byTarget.set(targetVersionId, [...(byTarget.get(targetVersionId) ?? []), review]);
      }
      return inferred.flatMap((version, i): Judgment[] => {
        const schema = schemas[i]?.content ?? null;
        if (schema?.resourceName === EPISODES) return [];
        const targeting = byTarget.get(version.versionId) ?? [];
        const verdictOf = (review: ObservationVersion) => (review.content as unknown as ReviewContent).verdict;
        const review =
          targeting.find(
            (candidate) =>
              reviewers.get(candidate.observerId)?.schemaId === PERSON_SCHEMA && verdictOf(candidate) !== "supersede",
          ) ?? null;
        const verdict = review ? (verdictOf(review) as "confirm" | "deny") : null;
        if (!review && targeting.some((candidate) => verdictOf(candidate) === "supersede")) return [];
        return [{ version, schema, review, verdict }];
      });
    },
    enabled: !!types.data,
    refetchInterval: JUDGMENTS_INTERVAL,
  });
}

/** 上次查看理解页面的时刻。 */
export function useSeen(): number {
  return useSyncExternalStore(subscribeSeen, readSeen);
}

export function isNew(judgment: Judgment, seen: number): boolean {
  return Date.parse(judgment.version.recordedAt) > seen;
}

/** 上次查看之后形成的判断数。 */
export function useNewJudgments(): number {
  const judgments = useJudgments();
  const seen = useSeen();
  return (judgments.data ?? []).filter((judgment) => isNew(judgment, seen)).length;
}

/** 撤回一份复核：删除这份 Review。 */
export function useWithdrawReview() {
  const source = useSource();
  const client = useQueryClient();
  return useMutation({
    mutationFn: (review: ObservationVersion) => source.deleteEntity(review.entityId),
    onSuccess: () => client.invalidateQueries(),
  });
}
