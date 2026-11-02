import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import type { Observation } from "@/api/types";
import { useInputSet } from "./queries";
import { isWhole, useReviewIndex, verdictOf } from "./useReview";

export interface ObservationStatus {
  /** 有生效的整条否定指向它。 */
  denied: boolean;
  /** 有 Observation 替换了它。 */
  replaced: boolean;
  /** 它的 InputSet 中有被否定的陈述。 */
  basisDenied: boolean;
}

/**
 * view=all 读到的陈述在图上的样子。按 id 读取不经过默认视图，所以从引用方读出事实：
 * 生效的 Review 由后端选出，这里只看它们指向谁。默认视图的结果直接用 basisDenied。
 */
export function useObservationStatus(observation: Observation | null): ObservationStatus {
  const source = useSource();
  const reviews = useReviewIndex();
  const input = useInputSet(observation?.inputSetId);
  const observationId = observation?.observationId;
  const referrers = useQuery({
    queryKey: ["referrers", observationId, "replacers"],
    queryFn: () => source.listObservationReferrers(observationId!, { view: "all", limit: 100 }),
    enabled: !!observationId,
    staleTime: 30_000,
  });
  const denied = (id: string) =>
    reviews.data?.get(id)?.some((review) => verdictOf(review) === "deny" && isWhole(review)) ?? false;
  return {
    denied: !!observationId && denied(observationId),
    replaced:
      referrers.data?.referrers.some(({ references }) =>
        references.some((location) => location.path.startsWith("/replaces/")),
      ) ?? false,
    basisDenied:
      observation?.basisDenied === true ||
      (input.data?.members.some((member) => reviews.data?.has(member) && denied(member)) ?? false),
  };
}
