import { useEffect, useMemo, useState, type ReactNode } from "react";
import { createFileRoute } from "@tanstack/react-router";
import type { Observation } from "@/api/types";
import { DeniedRanges, ReviewActions } from "@/components/record/statement";
import { useSaidBy } from "@/components/archive/nodes";
import { Token } from "@/components/token/Token";
import { Why } from "@/components/token/Why";
import { Message, PageHeader, Segmented } from "@/components/ui";
import { isNew, useJudgments, type Judgment } from "@/hooks/useJudgments";
import { useNow } from "@/hooks/useNow";
import { useInputSet } from "@/hooks/queries";
import { MEMBERSHIPS, type MembershipContent } from "@/lib/applications";
import { NAMES, RELATIONS, type RelationContent } from "@/lib/common";
import { readSeen, saveSeen } from "@/lib/preferences";
import { ago } from "@/lib/time";
import { businessTitle } from "@/lib/titles";
import { cn } from "@/lib/utils";

export const Route = createFileRoute("/understanding")({ component: UnderstandingPage });

type Tab = "pending" | "all";

/** 理解：模型形成的判断。它们直接生效，错的才需要否定。 */
function UnderstandingPage() {
  const judgments = useJudgments();
  const [seen] = useState(readSeen);
  const [tab, setTab] = useState<Tab>("pending");
  useEffect(() => saveSeen(Date.now()), []);

  const all = useMemo(() => judgments.data ?? [], [judgments.data]);
  const pending = useMemo(() => all.filter((judgment) => isNew(judgment, seen)), [all, seen]);
  const list = tab === "pending" ? pending : all;
  // 整条否定后判断离开默认视图，这里留到离开页面，让用户能马上撤回。
  const [denied, setDenied] = useState<{ judgment: Judgment; review: Observation }[]>([]);
  const onDenied = (judgment: Judgment, review: Observation) => setDenied((list) => [{ judgment, review }, ...list]);
  const onWithdrawn = (review: Observation) =>
    setDenied((list) => list.filter((item) => item.review.observationId !== review.observationId));
  const groups = useMemo(() => {
    const out = new Map<string, Judgment[]>();
    const newest = [...list].sort((a, b) => b.observation.recordedAt.localeCompare(a.observation.recordedAt));
    for (const judgment of newest) {
      const name = judgment.schema?.displayName ?? "判断";
      out.set(name, [...(out.get(name) ?? []), judgment]);
    }
    return [...out];
  }, [list]);

  return (
    <section>
      <PageHeader
        title="理解"
        summary={<p>判断直接生效，错的才需要否定；确认过的，模型以后不会替换。</p>}
        actions={
          <Segmented
            label="范围"
            value={tab}
            onChange={setTab}
            options={[
              { value: "pending", label: `待看 ${pending.length}` },
              { value: "all", label: `全部 ${all.length}` },
            ]}
          />
        }
      />
      {judgments.isError ? (
        <Message title="读不到判断" detail={judgments.error.message} mascot />
      ) : !judgments.data ? (
        <Message title="正在读取判断" />
      ) : !groups.length && !denied.length ? (
        <Message title={all.length ? "都看过了" : "还没有判断"} mascot />
      ) : (
        <div className="grid gap-8">
          {denied.length > 0 && (
            <section aria-label="刚否定的">
              <h2 className="mb-1 text-[13px] font-medium">
                刚否定的 <span className="text-fg-3 text-xs font-normal tabular-nums">{denied.length}</span>
              </h2>
              <ul>
                {denied.map(({ judgment, review }) => (
                  <Row
                    key={review.observationId}
                    judgment={{ ...judgment, review, verdict: "deny" }}
                    onWithdrawn={onWithdrawn}
                  />
                ))}
              </ul>
            </section>
          )}
          {groups.map(([name, items]) => (
            <section key={name} aria-label={name}>
              <h2 className="mb-1 text-[13px] font-medium">
                {name} <span className="text-fg-3 text-xs font-normal tabular-nums">{items.length}</span>
              </h2>
              <ul>
                {items.map((judgment) => (
                  <Row key={judgment.observation.observationId} judgment={judgment} onDenied={onDenied} />
                ))}
              </ul>
            </section>
          ))}
        </div>
      )}
    </section>
  );
}

/** 判断的一句话：判断中的事物写成记号。 */
function Sentence({ judgment }: { judgment: Judgment }): ReactNode {
  const { observation, schema } = judgment;
  const resourceName = schema?.resourceName;
  if (resourceName === NAMES) {
    const { thingId, name } = observation.content as { thingId: string; name: string };
    return (
      <>
        <Token thingId={thingId} size="sm" /> 叫 <strong className="font-medium">「{name}」</strong>
      </>
    );
  }
  if (resourceName === RELATIONS) {
    const { subjectId, predicate, objectId } = observation.content as unknown as RelationContent;
    return (
      <>
        <Token thingId={subjectId} size="sm" /> {predicate} <Token thingId={objectId} size="sm" />
      </>
    );
  }
  if (resourceName === MEMBERSHIPS) {
    const { identifierId, productId } = observation.content as unknown as MembershipContent;
    return (
      <>
        <Token thingId={identifierId} size="sm" /> 属于 <Token thingId={productId} size="sm" />
      </>
    );
  }
  const title = businessTitle(observation.content, schema?.fields, resourceName)?.text;
  return (
    <>
      {schema?.displayName ?? "判断"}
      {title && `：${title}`}
    </>
  );
}

function Row({
  judgment,
  onDenied,
  onWithdrawn,
}: {
  judgment: Judgment;
  onDenied?: (judgment: Judgment, review: Observation) => void;
  onWithdrawn?: (review: Observation) => void;
}) {
  const { observation, verdict } = judgment;
  const now = useNow(60_000);
  const saidBy = useSaidBy(observation);
  const inputs = useInputSet(observation.inputSetId);
  const members = inputs.data?.members.length;
  return (
    <li className="border-line grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-2 border-t py-3">
      <div className="min-w-0">
        <p
          className={cn(
            "text-[14px] leading-7",
            verdict === "deny" && !observation.deniedRanges?.length && "text-fg-3 line-through",
          )}
        >
          <Sentence judgment={judgment} />
        </p>
        <p className="text-fg-3 flex flex-wrap items-center gap-x-2 text-xs tabular-nums">
          <DeniedRanges observation={observation} />
          {observation.basisDenied && <span>依据已被否定</span>}
          <span>
            {[
              saidBy,
              `${ago(now - Date.parse(observation.recordedAt))}保存`,
              members !== undefined && `依据了 ${members} 个输入`,
            ]
              .filter(Boolean)
              .join(" · ")}
          </span>
          <Why observationId={observation.observationId} />
        </p>
      </div>
      <ReviewActions
        observation={observation}
        onDenied={onDenied && ((review) => onDenied(judgment, review))}
        onWithdrawn={onWithdrawn}
      />
    </li>
  );
}
