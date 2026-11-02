import { useSyncExternalStore } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import type { Schema, Observation } from "@/api/types";
import { REVIEWS, type ReviewContent, type Verdict } from "@/lib/common";
import { EPISODES } from "@/lib/episodes";
import { readSeen, subscribeSeen } from "@/lib/preferences";
import { MINUTE } from "@/lib/time";
import { observations } from "./useApplications";
import { pool, schemaOf } from "./useTypes";

/** 一条模型的判断。review 是用户本人对它生效的复核，用来写“已确认”“已否定”和撤回。 */
export interface Judgment {
  observation: Observation;
  schema: Schema | null;
  review: Observation | null;
  verdict: Verdict | null;
}

const JUDGMENTS_INTERVAL = MINUTE;

/** 模型形成的判断：默认视图中推断的 Observation，几件事除外。被否定和被替换的不出现，部分否定的带 deniedRanges。 */
export function useJudgments() {
  const source = useSource();
  const client = useQueryClient();
  return useQuery({
    queryKey: ["judgments"],
    queryFn: async (): Promise<Judgment[]> => {
      const [inferred, reviews] = await Promise.all([
        observations(source, { formation: "inferred" }),
        observations(source, { resourceName: REVIEWS, formation: "stated" }),
      ]);
      const schemas = await pool(inferred, 12, (observation) => schemaOf(client, source, observation));
      const byTarget = new Map<string, Observation>();
      // 默认视图只含生效的复核，后写的留下。
      for (const review of reviews) {
        const { targetId } = review.content as unknown as ReviewContent;
        if (targetId) byTarget.set(targetId, review);
      }
      return inferred.flatMap((observation, i): Judgment[] => {
        const schema = schemas[i]?.content ?? null;
        if (schema?.resourceName === EPISODES) return [];
        const review = byTarget.get(observation.observationId) ?? null;
        const verdict = review ? (review.content as unknown as ReviewContent).verdict : null;
        return [{ observation, schema, review, verdict }];
      });
    },
    refetchInterval: JUDGMENTS_INTERVAL,
  });
}

/** 上次查看理解页面的时刻。 */
export function useSeen(): number {
  return useSyncExternalStore(subscribeSeen, readSeen);
}

export function isNew(judgment: Judgment, seen: number): boolean {
  return Date.parse(judgment.observation.recordedAt) > seen;
}

/** 上次查看之后形成的判断数。 */
export function useNewJudgments(): number {
  const judgments = useJudgments();
  const seen = useSeen();
  return (judgments.data ?? []).filter((judgment) => isNew(judgment, seen)).length;
}
