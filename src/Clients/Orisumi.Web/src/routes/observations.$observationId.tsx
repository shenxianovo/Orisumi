import { useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, ChevronRight, Copy } from "lucide-react";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import type { DeleteResult, Observation } from "@/api/types";
import { Content } from "@/components/record/Content";
import { DeleteDialog } from "@/components/record/Panels";
import { ReferenceMap } from "@/components/record/ReferenceMap";
import { Reviews } from "@/components/record/Reviews";
import { DeniedRanges } from "@/components/record/statement";
import { InfoMark, Token } from "@/components/token/Token";
import { Button, Card, Message, Mono, SectionTitle } from "@/components/ui";
import { refreshView, useObservation } from "@/hooks/queries";
import { useDescribe } from "@/hooks/useItemInfo";
import { useNow } from "@/hooks/useNow";
import { useObservationStatus } from "@/hooks/useStatus";
import { readObservation } from "@/hooks/useTypes";
import { ago, dateTime, parseTime } from "@/lib/time";
import { Dependents } from "./things.$thingId";

export const Route = createFileRoute("/observations/$observationId")({ component: ObservationPage });

/** 一份 Observation 的全部内容、引用、替换、复核和删除。 */
function ObservationPage() {
  const { observationId } = Route.useParams();
  const observation = useObservation(observationId);
  const [deleted, setDeleted] = useState<DeleteResult | null>(null);

  if (deleted)
    return (
      <section>
        <Message title="已删除" detail={`移除了 ${deleted.deleted.length} 份说法`} mascot>
          <Link to="/records" className="text-fg-2 hover:text-fg text-[13px] underline underline-offset-4">
            回到原始记录
          </Link>
        </Message>
        <Dependents ids={deleted.dependents} />
      </section>
    );
  if (observation.isPending) return <Message title="读取中" mascot />;
  if (observation.isError) {
    const status = observation.error instanceof ApiError ? observation.error.status : 0;
    return (
      <Message
        title={
          status === 410
            ? "这份 Observation 已删除"
            : status === 404
              ? "没有这份 Observation"
              : "读不到这份 Observation"
        }
        detail={observationId}
        mascot
      />
    );
  }
  return <Page observation={observation.data} onDeleted={setDeleted} />;
}

function Page({ observation, onDeleted }: { observation: Observation; onDeleted: (result: DeleteResult) => void }) {
  const now = useNow(30_000);
  const { info } = useDescribe(observation);
  const [deleting, setDeleting] = useState(false);
  const title = info?.title ?? "…";
  const typeName = info?.typeName ?? "…";
  const recordedAt = parseTime(observation.recordedAt);
  const mark = (size: "lg" | "xl") =>
    info ? (
      <InfoMark info={info} size={size} framed />
    ) : (
      <span
        className={size === "xl" ? "bg-panel size-14 shrink-0 rounded-2xl" : "bg-panel size-10 shrink-0 rounded-[10px]"}
      />
    );

  return (
    <article>
      <nav aria-label="位置" className="text-fg-3 mb-3 flex items-center gap-1 text-xs">
        <Link to="/records" className="hover:text-fg">
          原始记录
        </Link>
        <ChevronRight className="size-3" />
        <Link to="/records" search={{ type: info?.schema?.content.resourceName }} className="hover:text-fg">
          {typeName}
        </Link>
      </nav>

      <header className="bg-card border-line shadow-card mb-4 rounded-2xl border p-4 sm:p-5">
        <div className="flex items-start gap-3.5">
          {mark("xl")}
          <div className="min-w-0 flex-1">
            <p className="text-fg-3 text-xs">{info ? `${info.roleName} · ${typeName}` : "读取中"}</p>
            <h1 className="text-[26px] leading-tight font-semibold tracking-[-0.01em] break-all">{title}</h1>
            <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1">
              <CopyId id={observation.observationId} />
              <span className="text-fg-3 text-xs" title={recordedAt === null ? undefined : dateTime(recordedAt)}>
                {recordedAt === null ? observation.recordedAt : `${ago(now - recordedAt)}记录`}
              </span>
              <span className="text-xs">
                <DeniedRanges observation={observation} />
              </span>
            </div>
          </div>
          <Button onClick={() => setDeleting(true)}>删除</Button>
        </div>
      </header>

      <Card className="mb-4">
        <SectionTitle>引用</SectionTitle>
        <ReferenceMap
          item={observation}
          fields={info?.schema?.content.fields}
          title={`这个${title}`}
          center={
            <>
              {mark("lg")}
              <span className="min-w-0">
                <span className="block truncate text-[13px] font-medium">{title}</span>
                <span className="text-fg-3 block text-[11px]">这份 Observation</span>
              </span>
            </>
          }
        />
      </Card>

      <Card className="mb-4">
        <SectionTitle>内容</SectionTitle>
        <Content item={observation} />
      </Card>

      <Replacement observation={observation} />

      <Card>
        <Reviews observation={observation} />
      </Card>

      <DeleteObservation
        observation={observation}
        open={deleting}
        onClose={() => setDeleting(false)}
        onDeleted={onDeleted}
      />
    </article>
  );
}

/** 替换：它替换的和替换它的说法。 */
function Replacement({ observation }: { observation: Observation }) {
  const source = useSource();
  const referrers = useQuery({
    queryKey: ["referrers", observation.observationId, "replacers"],
    queryFn: () => source.listObservationReferrers(observation.observationId, { view: "all", limit: 100 }),
  });
  const status = useObservationStatus(observation);
  const replacers = (referrers.data?.referrers ?? []).filter(({ references }) =>
    references.some((location) => location.path.startsWith("/replaces/")),
  );
  if (!observation.replaces.length && !status.replaced) return null;
  return (
    <Card className="mb-4">
      <SectionTitle>替换</SectionTitle>
      <div className="grid gap-2 text-[13px]">
        {observation.replaces.length > 0 && (
          <div className="flex flex-wrap items-center gap-1">
            <span className="text-fg-3 text-xs">它替换了</span>
            {observation.replaces.map((id) => (
              <Token key={id} observationId={id} size="sm" />
            ))}
          </div>
        )}
        {replacers.length > 0 && (
          <div className="flex flex-wrap items-center gap-1">
            <span className="text-fg-3 text-xs">替换它的</span>
            {replacers.map(({ observation: replacer }) => (
              <Token key={replacer.observationId} item={replacer} size="sm" />
            ))}
          </div>
        )}
      </div>
    </Card>
  );
}

/** 沿 replaces 读出会一起移除的说法。读不到的已经删除。 */
async function replacedChain(
  read: (id: string) => Promise<Observation | null>,
  observation: Observation,
): Promise<Observation[]> {
  const found = new Map<string, Observation>();
  const pending = [...observation.replaces];
  while (pending.length) {
    const id = pending.pop()!;
    if (found.has(id)) continue;
    const target = await read(id);
    if (!target) continue;
    found.set(id, target);
    pending.push(...target.replaces);
  }
  return [...found.values()];
}

function DeleteObservation({
  observation,
  open,
  onClose,
  onDeleted,
}: {
  observation: Observation;
  open: boolean;
  onClose: () => void;
  onDeleted: (result: DeleteResult) => void;
}) {
  const source = useSource();
  const client = useQueryClient();
  const chain = useQuery({
    queryKey: ["replaced-chain", observation.observationId],
    queryFn: () => replacedChain((id) => readObservation(client, source, id), observation),
    enabled: open,
  });
  const remove = useMutation({
    mutationFn: () => source.deleteObservation(observation.observationId),
    onSuccess: (result) => {
      onClose();
      onDeleted(result);
      refreshView(client);
    },
  });
  const others = chain.data ?? [];
  return (
    <DeleteDialog
      open={open}
      title="删除这份 Observation"
      busy={remove.isPending}
      error={remove.error?.message ?? null}
      onClose={onClose}
      onConfirm={() => remove.mutate()}
    >
      <p>
        {others.length ? `它替换的 ${others.length} 份说法一起移除，` : ""}
        不可恢复，重读来源也不会回来。替换它的和引用它的 Observation 保留，追溯到这里时显示不可读取。
      </p>
      {others.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1">
          {others.map((item) => (
            <Token key={item.observationId} item={item} size="sm" />
          ))}
        </div>
      )}
    </DeleteDialog>
  );
}

function CopyId({ id }: { id: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      title="复制 observationId"
      onClick={() => {
        void navigator.clipboard?.writeText(id).then(() => {
          setCopied(true);
          window.setTimeout(() => setCopied(false), 1500);
        });
      }}
      className="bg-panel/70 hover:bg-panel text-fg-2 inline-flex max-w-full cursor-pointer items-center gap-1.5 rounded-full px-2 py-0.5"
    >
      <Mono className="truncate text-[11px]">{id}</Mono>
      {copied ? <Check className="size-3 shrink-0" /> : <Copy className="size-3 shrink-0" />}
    </button>
  );
}
