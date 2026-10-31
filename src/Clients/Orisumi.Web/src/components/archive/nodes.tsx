import { useState, type ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import type { ObservationVersion } from "@/api/types";
import { InfoMark } from "@/components/token/Token";
import { Why } from "@/components/token/Why";
import { useStack } from "@/components/token/context";
import { useThingInfo } from "@/hooks/useEntityInfo";
import { useObservers } from "@/hooks/useObservers";
import { useStanding } from "@/hooks/useStanding";
import { FORMATION_NAMES } from "@/lib/common";
import { cn } from "@/lib/utils";

/** 节点和中间连一条线；data-wire 由 Archive 量出来画线。 */
export interface WireProps {
  label?: string | undefined;
  inferred?: boolean | undefined;
}

const LIMIT = 6;
const FOLDED = 3;

/** 一组节点。超过 6 个时先列出最近的 3 个，其余收成“还有 N 个”。 */
export function NodeGroup({ title, children }: { title: string; children: ReactNode[] }) {
  const [open, setOpen] = useState(false);
  const shown = open || children.length <= LIMIT ? children : children.slice(0, FOLDED);
  return (
    <section>
      <h2 className="text-fg-3 mb-1.5 text-xs">{title}</h2>
      <div className="grid gap-1.5">
        {shown}
        {shown.length < children.length && (
          <button
            type="button"
            onClick={() => setOpen(true)}
            className="text-fg-3 hover:text-fg cursor-pointer px-2.5 py-1 text-left text-xs"
          >
            还有 {children.length - shown.length} 个
          </button>
        )}
      </div>
    </section>
  );
}

function nodeStyle(inferred: boolean | undefined) {
  return cn(
    "bg-card flex min-w-0 items-center gap-2 rounded-lg border px-2.5 py-1.5 text-left",
    inferred ? "border-infer/60 border-dashed" : "border-line-2",
  );
}

/** 另一个事物：点它走到它的档案，“凭什么”推出连到它的那份陈述。 */
export function ThingNode({
  thingId,
  statement,
  label,
  inferred,
}: WireProps & { thingId: string; statement?: ObservationVersion | undefined }) {
  const { info, unreadable } = useThingInfo(thingId);
  return (
    <div data-wire="" data-label={label} data-inferred={inferred ? "" : undefined} className={nodeStyle(inferred)}>
      <Link
        to="/things/$thingId"
        params={{ thingId }}
        search={((prev: { date?: string }) => (prev.date ? { date: prev.date } : {})) as never}
        className="flex min-w-0 flex-1 items-center gap-2 hover:underline"
      >
        {info && <InfoMark info={info} size="md" />}
        <span className="min-w-0">
          <span className="block truncate text-[13px] leading-snug font-medium">
            {info?.title ?? (unreadable ? "读不到的事物" : "…")}
          </span>
          <span className="text-fg-3 block truncate text-[11px]">{info?.typeName}</span>
        </span>
      </Link>
      {statement && (
        <span className="shrink-0 text-[11px]">
          <Why entityId={statement.entityId} />
        </span>
      )}
    </div>
  );
}

/** 一份陈述：点它推出它的面板。被否定的画删除线，被取代的颜色变浅。 */
export function StatementNode({
  statement,
  title,
  detail,
  label,
  wired = true,
}: WireProps & { statement: ObservationVersion; title: string; detail?: string | undefined; wired?: boolean }) {
  const stack = useStack();
  const inferred = statement.formation === "inferred";
  const standing = useStanding(inferred ? statement : null, null);
  return (
    <button
      type="button"
      data-wire={wired ? "" : undefined}
      data-label={label}
      data-inferred={inferred ? "" : undefined}
      onClick={() => stack.open(statement.entityId, -1)}
      className={cn(nodeStyle(inferred), "hover:bg-accent-soft w-full cursor-pointer")}
    >
      <span className="min-w-0">
        <span
          className={cn(
            "block truncate text-[13px] leading-snug font-medium",
            standing.verdict === "deny" && "text-fg-3 line-through",
            standing.verdict === "supersede" && "text-fg-3",
          )}
        >
          {title}
        </span>
        {detail && <span className="text-fg-3 block truncate text-[11px]">{detail}</span>}
      </span>
    </button>
  );
}

/** Observer 的名字和形成方式，例如“前台状态 Observer · 直接读到”。 */
export function useSaidBy(statement: ObservationVersion): string {
  const observers = useObservers();
  const name = observers.data?.get(statement.observerId)?.identity.typeName ?? "读不到的 Observer";
  return `${name} · ${FORMATION_NAMES[statement.formation] ?? statement.formation}`;
}

export function NameNode({ statement }: { statement: ObservationVersion }) {
  const saidBy = useSaidBy(statement);
  const { name } = statement.content as { name: string };
  return <StatementNode statement={statement} title={name} detail={saidBy} label="名字" />;
}

export function ObserverNode({ thingId }: { thingId: string }) {
  const observers = useObservers();
  const stack = useStack();
  return (
    <button
      type="button"
      data-wire=""
      data-label="来源"
      onClick={() => stack.open(thingId, -1)}
      className={cn(nodeStyle(false), "hover:bg-accent-soft cursor-pointer")}
    >
      <span aria-hidden="true" className="border-fg-3 grid size-4 place-items-center rounded-full border-[1.5px]">
        <span className="bg-fg-3 size-1.5 rounded-full" />
      </span>
      <span className="truncate text-[13px] font-medium">
        {observers.data?.get(thingId)?.identity.typeName ?? "Observer"}
      </span>
    </button>
  );
}

/** 虚线的占位：例如“还没有名字”“还没归入产品”。 */
export function PendingNode({ title, detail, label }: WireProps & { title: string; detail?: string }) {
  return (
    <div
      data-wire=""
      data-label={label}
      className="border-line-3 text-fg-3 rounded-lg border border-dashed px-2.5 py-1.5 text-[13px]"
    >
      {title}
      {detail && <span className="block text-[11px]">{detail}</span>}
    </div>
  );
}
