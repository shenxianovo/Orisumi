import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import { isObservation, type ObservationVersion } from "@/api/types";
import { useNow } from "@/hooks/useNow";
import { useReview } from "@/hooks/useReview";
import { REVIEWS, type ReviewContent } from "@/lib/common";
import { ago } from "@/lib/time";
import { Token } from "../token/Token";

const VERDICTS = { confirm: "确认", deny: "否定", supersede: "取代" } as const;

/** 一份 Observation 的复核：列出引用它的 Review，用户可以确认或否定它，否定时写一句说明。 */
export function Reviews({ observation }: { observation: ObservationVersion }) {
  const source = useSource();
  const referrers = useQuery({
    queryKey: ["entity", observation.entityId, "referrers", REVIEWS],
    queryFn: () => source.listReferrers(observation.entityId, { resourceName: REVIEWS, limit: 100 }),
  });
  const now = useNow(60_000);
  const review = useReview();
  const [denying, setDenying] = useState(false);
  const [text, setText] = useState("");
  const reviews = (referrers.data?.referrers ?? [])
    .map((referrer) => referrer.version)
    .filter((version): version is ObservationVersion => isObservation(version));
  const submit = (verdict: "confirm" | "deny") =>
    review.mutate(
      { targetVersionId: observation.versionId, verdict, text: text.trim() || null },
      { onSuccess: () => (setDenying(false), setText("")) },
    );

  return (
    <section aria-label="复核">
      <div className="mb-1.5 flex items-baseline gap-2">
        <h3 className="text-fg-2 text-xs font-medium">复核</h3>
        <span className="flex-1" />
        <button
          type="button"
          disabled={review.isPending}
          onClick={() => submit("confirm")}
          className="text-fg-3 hover:text-fg cursor-pointer text-xs disabled:cursor-default"
        >
          确认
        </button>
        <button
          type="button"
          disabled={review.isPending}
          onClick={() => setDenying(!denying)}
          className="text-fg-3 hover:text-fg cursor-pointer text-xs disabled:cursor-default"
        >
          否定
        </button>
      </div>
      {denying && (
        <form
          className="mb-2 flex gap-1.5"
          onSubmit={(event) => {
            event.preventDefault();
            submit("deny");
          }}
        >
          <input
            value={text}
            onChange={(event) => setText(event.target.value)}
            placeholder="说明，可以不写"
            aria-label="否定的说明"
            className="bg-card border-line focus:border-line-3 min-w-0 flex-1 rounded-md border px-2 py-1 text-[13px] outline-none"
          />
          <button
            type="submit"
            disabled={review.isPending}
            className="bg-fg text-bg cursor-pointer rounded-md px-2.5 text-xs disabled:opacity-60"
          >
            否定
          </button>
        </form>
      )}
      {review.error && <p className="text-danger mb-1 text-xs">{(review.error as Error).message}</p>}
      {reviews.length === 0 ? (
        <p className="text-fg-3 text-[13px]">还没有复核</p>
      ) : (
        <ul className="grid gap-1">
          {reviews.map((version) => {
            const content = version.content as unknown as ReviewContent;
            return (
              <li key={version.versionId} className="flex flex-wrap items-center gap-1.5 text-[13px]">
                <span className="font-medium">{VERDICTS[content.verdict] ?? content.verdict}</span>
                <Token thingId={version.observerId} size="sm" />
                <span className="text-fg-3 text-xs">{ago(now - Date.parse(version.recordedAt))}</span>
                {content.text && <span className="text-fg-2 w-full pl-1">{content.text}</span>}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
