import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import { PERSON_SCHEMA, REVIEW_SCHEMA, USER_KEY, type Verdict } from "@/lib/common";

export interface ReviewInput {
  targetVersionId: string;
  verdict: Verdict;
  text?: string | null;
}

/**
 * 用户本人复核一条陈述：写一份 Review，形成方式是亲口陈述，事实时间未知，针对整条陈述。
 * 写入后重读页面上的数据，被否定的陈述随即离开当前理解。
 */
export function useReview() {
  const source = useSource();
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ targetVersionId, verdict, text }: ReviewInput) => {
      const response = await source.write({
        observer: { schemaId: PERSON_SCHEMA, key: USER_KEY },
        writes: [
          { schemaId: REVIEW_SCHEMA, formation: "stated", content: { targetVersionId, verdict, text: text ?? null } },
        ],
      });
      const result = response.results[0];
      if (result && "error" in result)
        throw new ApiError(result.error.status ?? 400, result.error.detail ?? "复核没有保存", result.error);
    },
    onSuccess: () => client.invalidateQueries(),
  });
}
