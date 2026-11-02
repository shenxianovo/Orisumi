import { useState } from "react";
import type { Observation } from "@/api/types";
import { Button } from "@/components/ui";
import { Why } from "@/components/token/Why";
import { useSaidBy } from "@/components/archive/nodes";
import { useObservationInfo, timeLabel } from "@/hooks/useItemInfo";
import { isWhole, useReview, useReviewIndex, useWithdraw, verdictOf } from "@/hooks/useReview";
import { cn } from "@/lib/utils";

/** 一份陈述的复核按钮：没有生效的复核时可以确认和否定；已复核的可以撤回。 */
export function ReviewActions({
  observation,
  onDenied,
  onWithdrawn,
}: {
  observation: Observation;
  /** 整条否定后这一行可能离开默认视图，调用方在这里留下撤回的入口。 */
  onDenied?: ((review: Observation) => void) | undefined;
  onWithdrawn?: ((review: Observation) => void) | undefined;
}) {
  const index = useReviewIndex();
  const review = useReview();
  const withdraw = useWithdraw();
  const [denying, setDenying] = useState(false);
  const [text, setText] = useState("");
  const reviews = index.data?.get(observation.observationId) ?? [];
  const latest = reviews.at(-1) ?? null;
  const busy = review.isPending || withdraw.isPending;
  const error = review.error ?? withdraw.error;
  return (
    <div className="grid justify-items-end gap-1">
      <div className="flex items-center gap-1.5">
        {latest ? (
          <>
            <span className="text-fg-3 text-xs">{verdictOf(latest) === "deny" ? "已否定" : "已确认"}</span>
            <Button
              disabled={busy}
              onClick={() => withdraw.withdraw(latest, { onSuccess: () => onWithdrawn?.(latest) })}
            >
              撤回
            </Button>
          </>
        ) : (
          <>
            <Button
              disabled={busy}
              onClick={() => review.mutate({ targetId: observation.observationId, verdict: "confirm" })}
            >
              确认
            </Button>
            <Button disabled={busy} onClick={() => setDenying(!denying)}>
              否定
            </Button>
          </>
        )}
      </div>
      {denying && !latest && (
        <form
          className="flex gap-1.5"
          onSubmit={(event) => {
            event.preventDefault();
            // 否定后这一行可能卸载，mutate 的回调不再执行，所以等 mutateAsync。
            void review
              .mutateAsync({ targetId: observation.observationId, verdict: "deny", text: text.trim() || null })
              .then((saved) => {
                setDenying(false);
                onDenied?.(saved);
              })
              .catch(() => undefined);
          }}
        >
          <input
            autoFocus
            value={text}
            onChange={(event) => setText(event.target.value)}
            onKeyDown={(event) => event.key === "Escape" && setDenying(false)}
            placeholder="说明，可以不写"
            aria-label="否定的说明"
            className="bg-card border-line focus:border-line-3 min-w-0 flex-1 rounded-md border px-2 py-1 text-[13px] outline-none"
          />
          <Button type="submit" disabled={busy}>
            否定
          </Button>
        </form>
      )}
      {error && <p className="text-danger text-xs">{error.message}</p>}
    </div>
  );
}

/** 被否定的时段，例如“15:00–16:00 已否定”。 */
export function DeniedRanges({ observation }: { observation: Observation }) {
  if (!observation.deniedRanges?.length) return null;
  return (
    <span className="text-fg-3">
      {observation.deniedRanges.map((range) => `${timeLabel(range.startAt, range.endAt)} 已否定`).join("、")}
    </span>
  );
}

/** 一条陈述在列表里的一行：一句话、谁说的、状态和复核。 */
export function StatementRow({ observation, status }: { observation: Observation; status?: string | undefined }) {
  const { info } = useObservationInfo(observation.observationId);
  const saidBy = useSaidBy(observation);
  const time = observation.startAt || observation.endAt ? timeLabel(observation.startAt, observation.endAt) : null;
  const denied = useReviewIndex()
    .data?.get(observation.observationId)
    ?.some((review) => verdictOf(review) === "deny" && isWhole(review));
  return (
    <li className="border-line grid grid-cols-[minmax(0,1fr)_auto] items-start gap-x-4 gap-y-1 border-t py-2.5 first:border-t-0">
      <div className="min-w-0">
        <p className={cn("text-[13px] leading-6", denied && "text-fg-3 line-through")}>
          <span className="text-fg-3">{info?.typeName ?? "…"} · </span>
          {info?.title}
        </p>
        <p className="text-fg-3 flex flex-wrap items-center gap-x-2 text-xs tabular-nums">
          <span>{[saidBy, time, status].filter(Boolean).join(" · ")}</span>
          <DeniedRanges observation={observation} />
          {observation.basisDenied && <span>依据已被否定</span>}
          <Why observationId={observation.observationId} />
        </p>
      </div>
      <ReviewActions observation={observation} />
    </li>
  );
}
