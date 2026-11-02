import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import type { Observation, Thing, ThingActivity } from "@/api/types";
import { InfoMark } from "@/components/token/Token";
import { Button, Message } from "@/components/ui";
import { useObservation, useSchemaRevision } from "@/hooks/queries";
import { useCatalog } from "@/hooks/useApplications";
import { useArchive, useWriteName, type ArchiveData } from "@/hooks/useArchive";
import { useActivity as useActivityPage } from "@/hooks/useArchive";
import { useDescribe, timeLabel, type ItemInfo } from "@/hooks/useItemInfo";
import { useTypes } from "@/hooks/useTypes";
import { useThingInfo } from "@/hooks/useItemInfo";
import { useObservers } from "@/hooks/useObservers";
import { useReview } from "@/hooks/useReview";
import { LOCAL_APPLICATIONS, PRODUCTS, type Catalog } from "@/lib/applications";
import { statementSpan, type ArchiveLink } from "@/lib/archive";
import { FORMATION_NAMES } from "@/lib/common";
import type { EpisodeContent } from "@/lib/episodes";
import { businessTitle } from "@/lib/titles";
import { dayTitle, duration, parseTime } from "@/lib/time";
import { NameNode, NodeGroup, ObserverNode, PendingNode, StatementNode, ThingNode } from "./nodes";

/** 档案：以一个事物为中心的织图。左边是怎样称呼它、谁看到过它，右边是和它有关的事物和发生过的事。 */
export function Archive({ thing }: { thing: Thing }) {
  const archive = useArchive(thing.thingId);
  const { info } = useDescribe(thing);
  const catalog = useCatalog();
  if (archive.isError) return <Message title="读不到这份档案" detail={archive.error.message} mascot />;
  if (!archive.data || !info) return <Message title="正在读取档案" />;
  return <Weave thing={thing} info={info} data={archive.data} catalog={catalog.data} />;
}

interface Wire {
  d: string;
  inferred: boolean;
  label: string | undefined;
  x: number;
  y: number;
  left: boolean;
}

/** 量出每个节点和中间的位置，画出连线。窄屏三栏上下排列时不画。 */
function useWires(data: unknown) {
  const box = useRef<HTMLDivElement>(null);
  const center = useRef<HTMLDivElement>(null);
  const [wires, setWires] = useState<Wire[]>([]);

  useLayoutEffect(() => {
    const element = box.current;
    if (!element) return;
    function measure() {
      const outer = element!.getBoundingClientRect();
      const middle = center.current!.getBoundingClientRect();
      if (middle.top - outer.top > 40 || middle.width > outer.width * 0.6) return setWires([]);
      const y2 = middle.top + middle.height / 2 - outer.top;
      setWires(
        [...element!.querySelectorAll<HTMLElement>("[data-wire]")].map((node) => {
          const rect = node.getBoundingClientRect();
          const left = rect.right <= middle.left;
          const x1 = (left ? rect.right : rect.left) - outer.left;
          const y1 = rect.top + rect.height / 2 - outer.top;
          const x2 = (left ? middle.left : middle.right) - outer.left;
          const mid = (x1 + x2) / 2;
          return {
            d: `M${x1},${y1} C${mid},${y1} ${mid},${y2} ${x2},${y2}`,
            inferred: "inferred" in node.dataset,
            label: node.dataset.label,
            x: left ? x1 + 6 : x1 - 6,
            y: y1 - 5,
            left,
          };
        }),
      );
    }
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [data]);

  return { box, center, wires };
}

function Weave({
  thing,
  info,
  data,
  catalog,
}: {
  thing: Thing;
  info: ItemInfo;
  data: ArchiveData;
  catalog: Catalog | undefined;
}) {
  const { box, center, wires } = useWires(data);
  const resourceName = info.schema?.content.resourceName;
  const device = data.links.find((link) => link.label === "device");

  const related: ReactNode[] = [
    ...data.sameAs.map((link) => <SameAsNode key={link.key} link={link} />),
    ...data.links.map((link) => (
      <ThingNode
        key={link.key}
        thingId={link.thingId}
        statement={link.statement}
        label={link.label}
        inferred={link.statement.formation === "inferred"}
      />
    )),
  ];
  if (resourceName === LOCAL_APPLICATIONS && catalog) {
    const product = catalog.productOf(thing);
    const membership = product?.memberships.find((m) =>
      catalog.installsOf(m.clue.thingId).some((install) => install.thing.thingId === thing.thingId),
    );
    related.push(
      product ? (
        <ThingNode
          key="product"
          thingId={product.thing.thingId}
          statement={membership?.observation}
          label="属于"
          inferred
        />
      ) : (
        <PendingNode key="product" title="还没归入产品" label="属于" />
      ),
    );
  }
  if (resourceName === PRODUCTS && catalog)
    for (const install of catalog.installs)
      if (catalog.productOf(install.thing)?.thing.thingId === thing.thingId)
        related.push(<ThingNode key={install.thing.thingId} thingId={install.thing.thingId} label="属于它" />);

  const experiences: ReactNode[] = [
    ...data.episodes.map((episode) => <EpisodeNode key={episode.observationId} episode={episode} />),
    ...data.activity.map((activity) => <Bundle key={activity.href} activity={activity} />),
  ];

  return (
    <div ref={box} className="relative">
      <svg aria-hidden="true" className="pointer-events-none absolute inset-0 size-full overflow-visible">
        {wires.map((wire, i) => (
          <g key={i}>
            <path
              d={wire.d}
              fill="none"
              stroke={wire.inferred ? "var(--infer)" : "var(--line-3)"}
              strokeWidth={1.2}
              strokeDasharray={wire.inferred ? "4 4" : undefined}
              opacity={0.7}
            />
            {wire.label && (
              <text x={wire.x} y={wire.y} textAnchor={wire.left ? "start" : "end"} fontSize={10.5} fill="var(--fg-3)">
                {wire.label}
              </text>
            )}
          </g>
        ))}
      </svg>
      <div className="relative grid items-start gap-x-16 gap-y-6 lg:grid-cols-[minmax(0,1fr)_minmax(220px,280px)_minmax(0,1fr)]">
        <div className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-5 max-lg:order-2">
          <NodeGroup title="怎样称呼它">
            {data.names.length
              ? data.names.map((name) => <NameNode key={name.observationId} statement={name} />)
              : [<PendingNode key="pending" title="还没有名字" />]}
          </NodeGroup>
          {data.observers.length > 0 && (
            <NodeGroup title="谁看到过它">
              {data.observers.map((observerId) => (
                <ObserverNode key={observerId} thingId={observerId} />
              ))}
            </NodeGroup>
          )}
        </div>
        <div
          ref={center}
          className="bg-card border-line-2 shadow-card rounded-2xl border p-5 text-center max-lg:order-1"
        >
          <Center thing={thing} info={info} deviceId={device?.thingId} />
        </div>
        <div className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-5 max-lg:order-3">
          {related.length > 0 && <NodeGroup title="和它有关的">{related}</NodeGroup>}
          {experiences.length > 0 && <NodeGroup title="经历">{experiences}</NodeGroup>}
        </div>
      </div>
    </div>
  );
}

/** 中间：记号、名字、类型和所在之处，名字由谁写下，以及“写一个名字”。 */
function Center({ thing, info, deviceId }: { thing: Thing; info: ItemInfo; deviceId: string | undefined }) {
  const device = useThingInfo(deviceId);
  return (
    <>
      <InfoMark info={info} size="xl" framed className="mx-auto" />
      <h1 className="mt-3 text-[20px] leading-snug font-semibold tracking-[-0.01em] break-words">{info.title}</h1>
      <p className="text-fg-3 mt-1 text-xs">
        {info.typeName}
        {device.info && ` · 在 ${device.info.title} 上`}
      </p>
      <NamedBy thing={thing} />
      <WriteName thing={thing} current={info.title} />
    </>
  );
}

function NamedBy({ thing }: { thing: Thing }) {
  const observation = useObservation(thing.nameObservationId);
  const observers = useObservers();
  const name = observation.data as Observation | undefined;
  if (!thing.name) return <p className="text-fg-3 mt-2 text-xs">还没有名字</p>;
  if (!name) return null;
  const identity = observers.data?.get(name.observerId)?.identity;
  return (
    <p className="text-fg-2 mt-2 text-xs">
      {identity?.typeName ?? "读不到的 Observer"} 这样称呼它
      {name.formation === "inferred" && `（${FORMATION_NAMES.inferred}）`}
    </p>
  );
}

function WriteName({ thing, current }: { thing: Thing; current: string }) {
  const write = useWriteName();
  const [value, setValue] = useState<string | null>(null);
  if (value === null)
    return (
      <div className="mt-3 flex justify-center">
        <Button onClick={() => setValue(thing.name ?? "")}>写一个名字</Button>
      </div>
    );
  return (
    <form
      className="mt-3"
      onSubmit={(event) => {
        event.preventDefault();
        const name = value.trim();
        if (!name || name === current) return setValue(null);
        write.mutate({ thingId: thing.thingId, name }, { onSuccess: () => setValue(null) });
      }}
    >
      <input
        autoFocus
        value={value}
        disabled={write.isPending}
        aria-label="名字"
        placeholder="你怎样称呼它"
        onChange={(event) => setValue(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === "Escape") {
            event.stopPropagation();
            setValue(null);
          }
        }}
        className="bg-page border-line-3 w-full rounded-md border px-2 py-1 text-center text-[13px] outline-none"
      />
      {write.error && <p className="text-danger mt-1 text-xs">{write.error.message}</p>}
    </form>
  );
}

function EpisodeNode({ episode }: { episode: Observation }) {
  const { title } = episode.content as unknown as EpisodeContent;
  const start = parseTime(episode.startAt);
  return (
    <StatementNode
      statement={episode}
      title={title}
      detail={`一件事 · ${start === null ? "" : `${dayTitle(start)} `}${timeLabel(episode.startAt, episode.endAt)}`}
      label="所在的事"
    />
  );
}

/** 另一个按 Same-As 同一的事物，“拆开”否定这条 Same-As。 */
function SameAsNode({ link }: { link: ArchiveLink }) {
  const review = useReview();
  return (
    <div className="min-w-0">
      <ThingNode
        thingId={link.thingId}
        statement={link.statement}
        label={link.label}
        inferred={link.statement.formation === "inferred"}
      />
      <div className="mt-0.5 flex items-center justify-end gap-2">
        {review.error && <span className="text-danger text-xs">{review.error.message}</span>}
        <button
          type="button"
          disabled={review.isPending}
          onClick={() => review.mutate({ targetId: link.statement.observationId, verdict: "deny" })}
          className="text-fg-3 hover:text-fg cursor-pointer text-xs disabled:cursor-default"
        >
          拆开
        </button>
      </div>
    </div>
  );
}

const BUNDLE_STEP = 20;

/** 一种类型的经历收成一束，写数量，点开用它的 href 逐条列出。 */
function Bundle({ activity }: { activity: ThingActivity }) {
  const [open, setOpen] = useState(false);
  const types = useTypes();
  const typeName = types.data?.byResourceName.get(activity.resourceName)?.content.displayName ?? "…";
  return (
    <div className="min-w-0">
      <button
        type="button"
        data-wire=""
        data-label={typeName}
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className="bg-card border-line-2 hover:bg-accent-soft w-full cursor-pointer rounded-lg border px-2.5 py-1.5 text-left"
      >
        <span className="block text-[13px] leading-snug font-medium tabular-nums">
          {activity.count.toLocaleString()} 份{typeName}
        </span>
        <span className="text-fg-3 block text-[11px]">{activity.role === "part" ? "它的一部分" : "提到它"}</span>
      </button>
      {open && <BundleItems activity={activity} />}
    </div>
  );
}

function BundleItems({ activity }: { activity: ThingActivity }) {
  const page = useActivityPage(activity.href, BUNDLE_STEP);
  const statements = page.data?.pages.flatMap((item) => item.observations) ?? [];
  return (
    <div className="mt-1.5 ml-4 grid gap-1">
      {statements.map((statement) => (
        <BundleItem key={statement.observationId} statement={statement} />
      ))}
      {page.isPending && <p className="text-fg-3 px-2.5 py-1 text-xs">读取中</p>}
      {page.hasNextPage && (
        <button
          type="button"
          onClick={() => void page.fetchNextPage()}
          className="text-fg-3 hover:text-fg cursor-pointer px-2.5 py-1 text-left text-xs"
        >
          还有 {(activity.count - statements.length).toLocaleString()} 个
        </button>
      )}
    </div>
  );
}

function BundleItem({ statement }: { statement: Observation }) {
  const schema = useSchemaRevision(statement.schemaRevisionId);
  const type = schema.data?.content;
  const title = businessTitle(statement.content, type?.fields, type?.resourceName)?.text;
  const span = statementSpan(statement);
  return (
    <StatementNode
      wired={false}
      statement={statement}
      title={timeLabel(statement.startAt, statement.endAt)}
      detail={[title, span > 0 && duration(span)].filter(Boolean).join(" · ")}
    />
  );
}
