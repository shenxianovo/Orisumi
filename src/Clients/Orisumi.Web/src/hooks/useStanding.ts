import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import { isObservation, type InputSet, type ObservationVersion, type Version } from "@/api/types";
import { PERSON_SCHEMA, REVIEWS, type ReviewContent } from "@/lib/common";
import { pool, readThings, readVersion } from "./useTypes";

export interface Standing {
  /** 整条被否定，或者被取代且没有用户确认。 */
  verdict: "deny" | "supersede" | null;
  /** 依据里有被否定的陈述，见 UNDERSTANDING-8。 */
  deniedInput: boolean;
}

const NONE: Standing = { verdict: null, deniedInput: false };

/**
 * 一份陈述在当前理解中的位置，按 UNDERSTANDING-4：针对整条的否定排除它；取代排除它，除非用户确认过。
 * 只否定一段的复核不改变它的位置。
 */
export function useStanding(observation: ObservationVersion | null, inputSet: Version<InputSet> | null): Standing {
  const source = useSource();
  const client = useQueryClient();
  const query = useQuery({
    queryKey: ["standing", observation?.entityId, inputSet?.versionId],
    queryFn: async (): Promise<Standing> => {
      const reviewsOf = async (entityId: string) =>
        (await source.listReferrers(entityId, { resourceName: REVIEWS, limit: 100 })).referrers
          .map((referrer) => referrer.version)
          .filter(isObservation);
      const denied = (reviews: ObservationVersion[]) =>
        reviews.some(
          (review) =>
            (review.content as unknown as ReviewContent).verdict === "deny" && !review.startAt && !review.endAt,
        );

      const reviews = await reviewsOf(observation!.entityId);
      const confirmers = reviews
        .filter((review) => (review.content as unknown as ReviewContent).verdict === "confirm")
        .map((review) => review.observerId);
      const people = await readThings(client, source, confirmers);
      const confirmed = confirmers.some((id) => people.get(id)?.schemaId === PERSON_SCHEMA);
      const superseded = reviews.some((review) => (review.content as unknown as ReviewContent).verdict === "supersede");

      const members = await pool(inputSet?.content.members ?? [], 8, (id) => readVersion(client, source, id));
      const inputs = await pool(
        members.filter((member) => member && isObservation(member)),
        8,
        async (member) => denied(await reviewsOf(member!.entityId)),
      );
      return {
        verdict: denied(reviews) ? "deny" : superseded && !confirmed ? "supersede" : null,
        deniedInput: inputs.some(Boolean),
      };
    },
    enabled: !!observation,
    staleTime: 30_000,
  });
  return query.data ?? NONE;
}
