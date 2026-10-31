import { useEffect, useMemo, useState, type ReactNode } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useSaidBy } from "@/components/archive/nodes";
import { Token } from "@/components/token/Token";
import { Why } from "@/components/token/Why";
import { Button, Message, PageHeader, Segmented } from "@/components/ui";
import { isNew, useJudgments, useWithdrawReview, type Judgment } from "@/hooks/useJudgments";
import { useNow } from "@/hooks/useNow";
import { useReview } from "@/hooks/useReview";
import { useVersion } from "@/hooks/queries";
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
  const groups = useMemo(() => {
    const out = new Map<string, Judgment[]>();
    const newest = [...list].sort((a, b) => b.version.recordedAt.localeCompare(a.version.recordedAt));
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
        summary={<p>判断直接生效，错的才需要否定；确认过的，模型以后不会取代。</p>}
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
      ) : !groups.length ? (
        <Message title={all.length ? "都看过了" : "还没有判断"} mascot />
      ) : (
        <div className="grid gap-8">
          {groups.map(([name, items]) => (
            <section key={name} aria-label={name}>
              <h2 className="mb-1 text-[13px] font-medium">
                {name} <span className="text-fg-3 text-xs font-normal tabular-nums">{items.length}</span>
              </h2>
              <ul>
                {items.map((judgment) => (
                  <Row key={judgment.version.versionId} judgment={judgment} />
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
  const { version, schema } = judgment;
  const resourceName = schema?.resourceName;
  if (resourceName === NAMES) {
    const { thingId, name } = version.content as { thingId: string; name: string };
    return (
      <>
        <Token thingId={thingId} size="sm" /> 叫 <strong className="font-medium">「{name}」</strong>
      </>
    );
  }
  if (resourceName === RELATIONS) {
    const { subjectId, predicate, objectId } = version.content as unknown as RelationContent;
    return (
      <>
        <Token thingId={subjectId} size="sm" /> {predicate} <Token thingId={objectId} size="sm" />
      </>
    );
  }
  if (resourceName === MEMBERSHIPS) {
    const { identifierId, productId } = version.content as unknown as MembershipContent;
    return (
      <>
        <Token thingId={identifierId} size="sm" /> 属于 <Token thingId={productId} size="sm" />
      </>
    );
  }
  const title = businessTitle(version.content, schema?.fields, resourceName)?.text;
  return (
    <>
      {schema?.displayName ?? "判断"}
      {title && `：${title}`}
    </>
  );
}

function Row({ judgment }: { judgment: Judgment }) {
  const { version, verdict, review: written } = judgment;
  const now = useNow(60_000);
  const review = useReview();
  const withdraw = useWithdrawReview();
  const saidBy = useSaidBy(version);
  const inputs = useVersion(version.inputSetVersionId);
  const members = (inputs.data?.content as { members?: string[] } | undefined)?.members?.length;
  const [denying, setDenying] = useState(false);
  const [text, setText] = useState("");
  const busy = review.isPending || withdraw.isPending;
  const error = review.error ?? withdraw.error;
  return (
    <li className="border-line grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-2 border-t py-3">
      <div className="min-w-0">
        <p className={cn("text-[14px] leading-7", verdict === "deny" && "text-fg-3 line-through")}>
          <Sentence judgment={judgment} />
        </p>
        <p className="text-fg-3 flex flex-wrap items-center gap-x-2 text-xs tabular-nums">
          <span>
            {[
              saidBy,
              `${ago(now - Date.parse(version.recordedAt))}保存`,
              members !== undefined && `依据了 ${members} 个输入`,
            ]
              .filter(Boolean)
              .join(" · ")}
          </span>
          <Why entityId={version.entityId} />
        </p>
      </div>
      <div className="flex items-center gap-1.5">
        {verdict && written ? (
          <>
            <span className="text-fg-3 text-xs">{verdict === "deny" ? "已否定" : "已确认"}</span>
            <Button disabled={busy} onClick={() => withdraw.mutate(written)}>
              撤回
            </Button>
          </>
        ) : (
          <>
            <Button
              disabled={busy}
              onClick={() => review.mutate({ targetVersionId: version.versionId, verdict: "confirm" })}
            >
              确认
            </Button>
            <Button disabled={busy} onClick={() => setDenying(!denying)}>
              否定
            </Button>
          </>
        )}
      </div>
      {denying && !verdict && (
        <form
          className="col-span-2 flex gap-1.5"
          onSubmit={(event) => {
            event.preventDefault();
            review.mutate(
              { targetVersionId: version.versionId, verdict: "deny", text: text.trim() || null },
              { onSuccess: () => setDenying(false) },
            );
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
      {error && <p className="text-danger col-span-2 text-xs">{error.message}</p>}
    </li>
  );
}
