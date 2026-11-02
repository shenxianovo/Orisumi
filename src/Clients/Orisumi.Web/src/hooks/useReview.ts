import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import type { Observation } from "@/api/types";
import { PERSON_SCHEMA, REVIEW_SCHEMA, REVIEWS, USER_KEY, type ReviewContent, type Verdict } from "@/lib/common";
import { refreshView } from "./queries";
import { observations } from "./useApplications";

export interface ReviewInput {
  targetId: string;
  verdict: Verdict;
  text?: string | null;
}

/**
 * 用户本人复核一条陈述：写一份 Review，形成方式是 stated，事实时间未知，针对整条陈述。
 * 否定一份 Review 就是撤回它。写入后重读页面上的数据，默认视图由后端重新算出。
 */
export function useReview() {
  const source = useSource();
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ targetId, verdict, text }: ReviewInput): Promise<Observation> => {
      const response = await source.write({
        observer: { schemaId: PERSON_SCHEMA, key: USER_KEY },
        writes: [
          {
            schemaId: REVIEW_SCHEMA,
            formation: "stated",
            content: { verdict, targetId, scope: null, text: text ?? null },
          },
        ],
      });
      const result = response.results[0]!;
      if ("error" in result)
        throw new ApiError(result.error.status ?? 400, result.error.detail ?? "复核没有保存", result.error);
      if ("skipped" in result) throw new ApiError(409, "用户本人已被删除，复核没有保存");
      return result.observation;
    },
    onSuccess: () => refreshView(client),
  });
}

/** 撤回一份复核：否定这份 Review，见 REVIEW-6。 */
export function useWithdraw() {
  const review = useReview();
  return {
    ...review,
    withdraw: (reviewObservation: Observation, options?: { onSuccess?: () => void }) =>
      review.mutate({ targetId: reviewObservation.observationId, verdict: "deny" }, options),
  };
}

export function verdictOf(review: Observation): Verdict {
  return (review.content as unknown as ReviewContent).verdict;
}

/** 整条的复核：两个边界都未知。 */
export function isWhole(review: Observation): boolean {
  return !review.startAt && !review.endAt;
}

/** 默认视图中的 Review 按目标归组，只含生效的复核，新的在后。 */
export function useReviewIndex() {
  const source = useSource();
  return useQuery({
    queryKey: ["reviews"],
    queryFn: async () => {
      const index = new Map<string, Observation[]>();
      for (const review of await observations(source, { resourceName: REVIEWS })) {
        const { targetId } = review.content as unknown as ReviewContent;
        if (targetId) index.set(targetId, [...(index.get(targetId) ?? []), review]);
      }
      return index as ReadonlyMap<string, Observation[]>;
    },
    staleTime: 10_000,
  });
}
