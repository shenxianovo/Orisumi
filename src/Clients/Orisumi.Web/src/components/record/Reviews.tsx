import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import type { Observation } from "@/api/types";
import { useNow } from "@/hooks/useNow";
import { useReview, useWithdraw } from "@/hooks/useReview";
import { REVIEWS, type ReviewContent } from "@/lib/common";
import { ago } from "@/lib/time";
import { Token } from "../token/Token";

const VERDICTS = { confirm: "确认", deny: "否定" } as const;

/** 一份 Observation 的复核：列出引用它的 Review，用户可以确认或否定它，否定时写一句说明；生效的复核可以撤回。 */
export function Reviews({ observation }: { observation: Observation }) {
  const source = useSource();
  const observationId = observation.observationId;
  const every = useQuery({
    queryKey: ["referrers", observationId, REVIEWS, "all"],
    queryFn: () => source.listObservationReferrers(observationId, { resourceName: REVIEWS, view: "all", limit: 100 }),
  });
  const current = useQuery({
    queryKey: ["referrers", observationId, REVIEWS, "current"],
    queryFn: () => source.listObservationReferrers(observationId, { resourceName: REVIEWS, limit: 100 }),
  });
  const now = useNow(60_000);
  const review = useReview();
  const withdraw = useWithdraw();
  const [denying, setDenying] = useState(false);
  const [text, setText] = useState("");
  const effective = new Set(current.data?.referrers.map((referrer) => referrer.observation.observationId));
  const reviews = (every.data?.referrers ?? [])
    .map((referrer) => referrer.observation)
    .filter((item) => (item.content as unknown as ReviewContent).targetId === observationId);
  const submit = (verdict: "confirm" | "deny") =>
    review.mutate(
      { targetId: observationId, verdict, text: text.trim() || null },
      { onSuccess: () => (setDenying(false), setText("")) },
    );
  const error = review.error ?? withdraw.error;

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
      {error && <p className="text-danger mb-1 text-xs">{error.message}</p>}
      {reviews.length === 0 ? (
        <p className="text-fg-3 text-[13px]">还没有复核</p>
      ) : (
        <ul className="grid gap-1">
          {reviews.map((item) => {
            const content = item.content as unknown as ReviewContent;
            const live = effective.has(item.observationId);
            return (
              <li key={item.observationId} className="flex flex-wrap items-center gap-1.5 text-[13px]">
                <span className={live ? "font-medium" : "text-fg-3 font-medium line-through"}>
                  {VERDICTS[content.verdict]}
                </span>
                <Token thingId={item.observerId} size="sm" />
                <span className="text-fg-3 text-xs">{ago(now - Date.parse(item.recordedAt))}</span>
                <span className="flex-1" />
                {live ? (
                  <button
                    type="button"
                    disabled={withdraw.isPending}
                    onClick={() => withdraw.withdraw(item)}
                    className="text-fg-3 hover:text-fg cursor-pointer text-xs disabled:cursor-default"
                  >
                    撤回
                  </button>
                ) : (
                  <span className="text-fg-3 text-xs">已撤回</span>
                )}
                {content.text && <span className="text-fg-2 w-full pl-1">{content.text}</span>}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
