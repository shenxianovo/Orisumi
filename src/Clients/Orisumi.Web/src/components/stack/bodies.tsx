import { useMemo, type ReactNode } from "react";
import { Link, useSearch } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import { readAllThings } from "@/api/client";
import {
  handleOf,
  isInputSet,
  isObservation,
  isSchemaRevision,
  isThing,
  type FieldDefinition,
  type InputSet,
  type Item,
  type Observation,
  type Schema,
  type Thing,
} from "@/api/types";
import { Blueprint } from "@/components/record/Content";
import { Mono } from "@/components/ui";
import { useInputSet, useReferrers, useSchemaRevision } from "@/hooks/queries";
import { productGroup, useCatalog } from "@/hooks/useApplications";
import { useDay } from "@/hooks/useDay";
import { type ItemInfo } from "@/hooks/useItemInfo";
import { useNow } from "@/hooks/useNow";
import { useObservationStatus } from "@/hooks/useStatus";
import { useTypes } from "@/hooks/useTypes";
import { LOCAL_APPLICATIONS, PRODUCTS } from "@/lib/applications";
import { FORMATION_NAMES, OBSERVER_DESCRIPTIONS } from "@/lib/common";
import { groupThings, summarize, type Segment } from "@/lib/day";
import { outgoingReferences } from "@/lib/references";
import { differences } from "@/lib/roles";
import { ago, clock, dayRange, dayTitle, duration, parseTime } from "@/lib/time";
import { thingTitle } from "@/lib/titles";
import { cn } from "@/lib/utils";
import { Reviews } from "../record/Reviews";
import { Token } from "../token/Token";
import { Membership, ProductSections } from "./application";

export function PanelBody({ info }: { info: ItemInfo }) {
  const { item } = info;
  return (
    <div className="panel-body grid gap-6">
      {isThing(item) && info.role === "thing" && <ThingBody info={info} thing={item} />}
      {isObservation(item) && <ObservationBody info={info} observation={item} />}
      {isThing(item) && info.role === "source" && <SourceBody thing={item} />}
      {info.role === "definition" && <DefinitionBody info={info} />}
      {isInputSet(item) && <EvidenceBody inputSet={item} />}
      <Raw item={item} role={info.role} />
    </div>
  );
}

function Section({ title, aside, children }: { title: string; aside?: ReactNode; children: ReactNode }) {
  return (
    <section>
      <div className="mb-1.5 flex items-baseline gap-2">
        <h3 className="text-fg-2 text-xs font-medium">{title}</h3>
        <span className="flex-1" />
        {aside && <span className="text-fg-3 text-xs">{aside}</span>}
      </div>
      {children}
    </section>
  );
}

function Empty({ children }: { children: ReactNode }) {
  return <p className="text-fg-3 text-[13px]">{children}</p>;
}

/** 面板读的日期：日页面的 date，没有时是今天。 */
function useDateKey(): string {
  const search = useSearch({ strict: false }) as { date?: string };
  const now = useNow(60_000);
  return search.date ?? dayRange(now).key;
}

// —— 事物 ——

function ThingBody({ info, thing }: { info: ItemInfo; thing: Thing }) {
  const key = useDateKey();
  const day = useDay(key);
  const catalog = useCatalog();
  const resourceName = info.schema?.content.resourceName;
  const isProduct = resourceName === PRODUCTS;
  // 产品的用量是归到它的各个本机应用的合计。
  const experiences = useMemo(
    () =>
      isProduct && catalog.data
        ? groupThings(day.data?.experiences ?? [], productGroup(catalog.data))
        : (day.data?.experiences ?? []),
    [isProduct, catalog.data, day.data],
  );
  const summary = useMemo(
    () => (day.data ? summarize(experiences, day.range.start, day.range.end) : null),
    [day.data, experiences, day.range.start, day.range.end],
  );
  const row = summary?.things.find((r) => r.key === thing.thingId);
  const seenBy = useMemo(
    () => [
      ...new Set(
        experiences.filter((e) => e.things.some((t) => t.thing.thingId === thing.thingId)).map((e) => e.observerId),
      ),
    ],
    [experiences, thing.thingId],
  );

  return (
    <>
      <Section title={dayTitle(day.range.start)} aside={row ? `${row.segments.length} 段` : undefined}>
        {day.loading ? (
          <Empty>读取中</Empty>
        ) : !row ? (
          <Empty>这一天没有出现</Empty>
        ) : (
          <>
            <p className="text-[26px] leading-tight font-medium tabular-nums">{duration(row.total)}</p>
            <DayStrip
              segments={row.segments}
              from={day.range.start}
              to={day.range.end}
              color={info.color ?? "var(--fg-2)"}
            />
            <ul className="mt-2 grid gap-0.5">
              {row.segments
                .slice(-6)
                .reverse()
                .map((segment) => {
                  const experience = experiences.find((e) => e.observationId === segment.observationId);
                  return (
                    <li key={segment.observationId} className="flex items-center gap-2 text-[13px]">
                      {experience ? (
                        <Token item={experience.observation} size="sm" />
                      ) : (
                        <span className="tabular-nums">{clock(segment.start)}</span>
                      )}
                      <span className="flex-1" />
                      <span className="text-fg-3 text-xs tabular-nums">{duration(segment.end - segment.start)}</span>
                    </li>
                  );
                })}
            </ul>
          </>
        )}
      </Section>
      {isProduct ? (
        <ProductSections product={thing} />
      ) : (
        <>
          {resourceName === LOCAL_APPLICATIONS && <Membership install={thing} />}
          <Names thing={thing} />
          <Siblings info={info} />
        </>
      )}
      <Keys thing={thing} />
      {seenBy.length > 0 && (
        <Section title="谁看到过它">
          <div className="flex flex-wrap gap-1">
            {seenBy.map((observerId) => (
              <Token key={observerId} thingId={observerId} size="sm" />
            ))}
          </div>
        </Section>
      )}
    </>
  );
}

/** 事物只有身份：认出它的识别依据。 */
function Keys({ thing }: { thing: Thing }) {
  if (!thing.keys.length) return null;
  return (
    <Section title="识别依据">
      <ul className="grid gap-1">
        {thing.keys.map((key) => (
          <li key={`${key.scope}|${key.scheme}|${key.value}`} className="min-w-0 text-[13px]">
            <span className="text-fg-3 font-mono text-[11px]">{key.scheme}</span>
            <span className="block font-mono text-xs break-all">{key.value}</span>
          </li>
        ))}
      </ul>
    </Section>
  );
}

/** 一天 24 小时的细条，段落画在各自的时间上。 */
export function DayStrip({
  segments,
  from,
  to,
  color,
  compact,
}: {
  segments: readonly Pick<Segment, "start" | "end">[];
  from: number;
  to: number;
  color: string;
  /** 只画细条，不写时刻。 */
  compact?: boolean;
}) {
  const span = to - from;
  return (
    <div className="mt-2">
      <div className={cn("bg-line relative overflow-hidden rounded-full", compact ? "h-1.5" : "h-2")}>
        {segments.map((segment, i) => (
          <span
            key={i}
            className="absolute inset-y-0"
            style={{
              left: `${((segment.start - from) / span) * 100}%`,
              width: `max(1px, ${((segment.end - segment.start) / span) * 100}%)`,
              background: color,
            }}
          />
        ))}
      </div>
      <div className={cn("text-fg-3 mt-0.5 flex justify-between text-[10px] tabular-nums", compact && "hidden")}>
        <span>0</span>
        <span>6</span>
        <span>12</span>
        <span>18</span>
        <span>24</span>
      </div>
    </div>
  );
}

/** 提到它的 Observation 里读到的名字，例如 Name 和前台时段的 name；以及引用它的各种类型。 */
function Names({ thing }: { thing: Thing }) {
  const referrers = useReferrers(thing);
  const pages = referrers.data?.pages ?? [];
  const all = pages.flatMap((page) => page.referrers);
  const names = new Map<string, number>();
  const types = new Map<string, number>();
  for (const { observation } of all) {
    types.set(observation.schemaRevisionId, (types.get(observation.schemaRevisionId) ?? 0) + 1);
    const content = (observation.content ?? {}) as Record<string, unknown>;
    const name = typeof content.name === "string" ? content.name : null;
    if (name) names.set(name, (names.get(name) ?? 0) + 1);
  }
  const more = referrers.hasNextPage ? "+" : "";
  if (referrers.isPending) return null;
  return (
    <>
      {names.size > 0 && (
        <Section title="叫过的名字">
          <ul className="flex flex-wrap gap-1.5">
            {[...names.entries()]
              .sort((a, b) => b[1] - a[1])
              .map(([name, count]) => (
                <li key={name} className="bg-panel rounded-md px-2 py-0.5 text-[13px]">
                  {name}
                  <span className="text-fg-3 ml-1.5 text-xs tabular-nums">
                    {count}
                    {more}
                  </span>
                </li>
              ))}
          </ul>
        </Section>
      )}
      <Section title="被引用">
        {all.length === 0 ? (
          <Empty>没有其他记录引用它</Empty>
        ) : (
          <ul className="grid gap-1">
            {[...types.entries()].map(([revision, count]) => (
              <li key={revision} className="flex items-center gap-2 text-[13px]">
                <TypeName schemaRevisionId={revision} />
                <span className="flex-1" />
                <span className="text-fg-3 text-xs tabular-nums">
                  {count}
                  {more} 份
                </span>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </>
  );
}

function TypeName({ schemaRevisionId }: { schemaRevisionId: string }) {
  const schema = useSchemaRevision(schemaRevisionId);
  return <span>{schema.data?.content.displayName ?? "…"}</span>;
}

/** 同名的其他事物，例如同一个 bundle id 的另一个安装。并排列出，写出它们识别依据里不同的那一段。 */
function Siblings({ info }: { info: ItemInfo }) {
  const source = useSource();
  const resourceName = info.schema?.content.resourceName;
  const list = useQuery({
    queryKey: ["things", { resourceName }, "all"],
    queryFn: () =>
      readAllThings((after) =>
        source.listThings({ resourceName: resourceName!, limit: 1000, ...(after ? { after } : {}) }),
      ),
    enabled: !!resourceName,
  });
  const group = (list.data ?? []).filter((thing) => thingTitle(thing, info.typeName) === info.title);
  if (group.length < 2) return null;
  const details = differences(group.map((thing) => thing.keys));
  return (
    <Section title={`同名的${info.typeName}`} aside={`${group.length} 个`}>
      <ul className="grid gap-0.5">
        {group.map((thing, i) => (
          <li key={thing.thingId} className="flex items-center">
            <Token item={thing} size="sm" detail={details[i]} />
            {thing.thingId === info.id && <span className="text-fg-3 ml-1 text-xs">· 这个</span>}
          </li>
        ))}
      </ul>
    </Section>
  );
}

// —— 经历和陈述 ——

/** Observation 的固定成员，内容里的“关于”不列它们。 */
const MEMBERS = new Set(["observerId", "speakerId", "observerDescriptionId"]);

function ObservationBody({ info, observation }: { info: ItemInfo; observation: Observation }) {
  const fields = info.schema?.content.fields;
  const now = useNow(1000);
  const start = parseTime(observation.startAt);
  const end = parseTime(observation.endAt);
  const references = outgoingReferences(observation, fields).filter(
    (reference) => !MEMBERS.has(reference.path) && !reference.path.startsWith("replaces["),
  );
  const status = useObservationStatus(observation);

  return (
    <>
      {(start !== null || end !== null) && (
        <Section title={dayTitle((start ?? end)!)}>
          <p className="text-[22px] leading-tight font-medium tabular-nums">
            {start !== null && end !== null && end > start ? `${clock(start)} – ${clock(end)}` : clock((start ?? end)!)}
          </p>
          {start !== null && end !== null && end > start && (
            <p className="text-fg-2 mt-0.5 text-[13px]">{duration(end - start)}</p>
          )}
        </Section>
      )}

      <Section title="内容">
        {references.length > 0 && (
          <div className="mb-1.5 flex flex-wrap items-center gap-1 text-[13px]">
            <span className="text-fg-3">关于</span>
            {references.map((reference) =>
              reference.target === "thing" ? (
                <Token key={reference.path} thingId={reference.id} />
              ) : (
                <Token key={reference.path} observationId={reference.id} />
              ),
            )}
          </div>
        )}
        <Fields content={observation.content} schema={null} fields={fields} skipReferences />
      </Section>

      <Section title="由谁记下">
        <div className="flex flex-wrap items-center gap-1.5 text-[13px]">
          <Token thingId={observation.observerId} />
          <span className="text-fg-3 text-xs">
            {FORMATION_NAMES[observation.formation] ?? observation.formation} ·{" "}
            {ago(now - Date.parse(observation.recordedAt))}
            保存
          </span>
        </div>
        {observation.speakerId && (
          <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[13px]">
            <span className="text-fg-3 text-xs">说话人</span>
            <Token thingId={observation.speakerId} size="sm" />
          </div>
        )}
        <Replacements observation={observation} replaced={status.replaced} />
      </Section>
      {observation.inputSetId && (
        <Section title="依据" aside={status.basisDenied ? "依据已被否定" : undefined}>
          <Members inputSetId={observation.inputSetId} />
        </Section>
      )}
      <Reviews observation={observation} />
      <ReferrerTokens item={observation} title="对它的陈述" />
    </>
  );
}

/** 它替换了哪些说法，以及它是否已被替换。 */
function Replacements({ observation, replaced }: { observation: Observation; replaced: boolean }) {
  if (!observation.replaces.length && !replaced) return null;
  return (
    <div className="text-fg-3 mt-1 grid gap-1 text-xs">
      {replaced && <p>已被新的说法替换</p>}
      {observation.replaces.length > 0 && (
        <div className="flex flex-wrap items-center gap-1">
          <span>替换了</span>
          {observation.replaces.map((id) => (
            <Token key={id} observationId={id} size="sm" />
          ))}
        </div>
      )}
    </div>
  );
}

function Members({ inputSetId }: { inputSetId: string }) {
  const set = useInputSet(inputSetId);
  if (set.isError) return <Empty>不可读取</Empty>;
  if (!set.data) return null;
  return (
    <ul className="border-line ml-2 grid gap-0.5 border-l pl-2.5">
      {set.data.members.map((member) => (
        <li key={member}>
          <Token observationId={member} size="sm" />
        </li>
      ))}
    </ul>
  );
}

function ReferrerTokens({ item, title }: { item: Observation; title: string }) {
  const referrers = useReferrers(item);
  const all = referrers.data?.pages.flatMap((p) => p.referrers) ?? [];
  if (!all.length) return null;
  return (
    <Section title={title} aside={`${all.length}${referrers.hasNextPage ? "+" : ""}`}>
      <div className="flex flex-wrap gap-1">
        {all.slice(0, 12).map(({ observation }) => (
          <Token key={observation.observationId} item={observation} size="sm" />
        ))}
      </div>
    </Section>
  );
}

// —— 来源 ——

function SourceBody({ thing }: { thing: Thing }) {
  const key = useDateKey();
  const day = useDay(key);
  const now = useNow(5000);
  const description = useDescription(thing.thingId);
  const written = (day.data?.experiences ?? []).filter((e) => e.observerId === thing.thingId);
  const points = (day.data?.points ?? []).find((p) => p.key === thing.thingId);
  const latest = Math.max(...written.map((e) => e.writtenAt), ...(points?.times ?? []), 0);
  return (
    <>
      {description && (
        <Section title="描述">
          <div className="mb-1.5">
            <Token item={description} size="sm" />
          </div>
          <DescriptionFields description={description} />
        </Section>
      )}
      <Section title={dayTitle(day.range.start)}>
        {day.loading ? (
          <Empty>读取中</Empty>
        ) : !written.length && !points ? (
          <Empty>这一天没有写入</Empty>
        ) : (
          <>
            <p className="text-[22px] leading-tight font-medium tabular-nums">
              {written.length + (points?.times.length ?? 0)}
              <span className="text-fg-2 ml-1 text-[13px] font-normal">份 Observation</span>
            </p>
            {latest > 0 && <p className="text-fg-3 mt-0.5 text-xs">最近一次 {ago(now - latest)}</p>}
            {written.length > 0 && (
              <DayStrip
                segments={written.map((e) => ({ start: e.start, end: e.end }))}
                from={day.range.start}
                to={day.range.end}
                color="var(--fg-2)"
              />
            )}
          </>
        )}
      </Section>
    </>
  );
}

/** 这个 Observer 自己的描述：默认视图中它写下的最新一份。 */
function useDescription(observerId: string) {
  const source = useSource();
  const query = useQuery({
    queryKey: ["observer-description", observerId],
    queryFn: async () =>
      (await source.listObservations({ observerId, resourceName: OBSERVER_DESCRIPTIONS, order: "desc", limit: 1 }))
        .observations[0] ?? null,
    staleTime: 60_000,
  });
  return query.data ?? null;
}

function DescriptionFields({ description }: { description: Observation }) {
  const schema = useSchemaRevision(description.schemaRevisionId);
  return <Fields content={description.content} schema={null} fields={schema.data?.content.fields} />;
}

// —— 定义和依据 ——

function DefinitionBody({ info }: { info: ItemInfo }) {
  const types = useTypes();
  if (!isSchemaRevision(info.item)) return null;
  const schema: Schema = info.item.content;
  const edges = types.data?.edges ?? [];
  const name = (key: string) => types.data?.nodes.find((n) => n.key === key)?.name ?? key;
  const out = edges.filter((e) => e.from === schema.resourceName);
  const into = edges.filter((e) => e.to === schema.resourceName);
  return (
    <>
      {schema.description && <p className="text-fg-2 text-[13px]">{schema.description}</p>}
      <Section title="字段">
        <Blueprint fields={schema.fields} />
      </Section>
      {(out.length > 0 || into.length > 0) && (
        <Section title="引用">
          <ul className="grid gap-1 text-[13px]">
            {out.map((e) => (
              <li key={`o${e.to}${e.label}`}>
                <Mono className="text-fg-2">{e.label}</Mono> → {name(e.to)}
              </li>
            ))}
            {into.map((e) => (
              <li key={`i${e.from}${e.label}`}>
                {name(e.from)} <Mono className="text-fg-2">{e.label}</Mono> → 这里
              </li>
            ))}
          </ul>
        </Section>
      )}
      <Link
        to="/records"
        search={{ type: schema.resourceName }}
        className="text-fg-2 hover:text-fg text-[13px] underline-offset-4 hover:underline"
      >
        查看这个类型的记录 →
      </Link>
    </>
  );
}

function EvidenceBody({ inputSet }: { inputSet: InputSet }) {
  return (
    <Section title="输入">
      <Members inputSetId={inputSet.inputSetId} />
    </Section>
  );
}

// —— 字段 ——

const COMPACT_ITEMS = 3;

/** 内容的字段表。limit 限制显示的行数，多出的写成“还有 N 个字段”。 */
export function Fields({
  content: raw,
  schema,
  fields: given,
  skipReferences,
  skip,
  limit,
}: {
  content: unknown;
  schema: { content: Schema } | null;
  fields?: Record<string, FieldDefinition> | undefined;
  skipReferences?: boolean;
  /** 已经写在别处的字段，例如作为名字的那个。 */
  skip?: string | undefined;
  limit?: number;
}) {
  const fields = given ?? schema?.content.fields ?? {};
  const content = (raw ?? {}) as Record<string, unknown>;
  const names = [...Object.keys(fields), ...Object.keys(content).filter((k) => !(k in fields))].filter(
    (name) => content[name] !== undefined && name !== skip && !(skipReferences && fields[name]?.type === "reference"),
  );
  if (!names.length) return null;
  const shown = limit === undefined ? names : names.slice(0, limit);
  return (
    <>
      <dl className="grid gap-1.5">
        {shown.map((name) => (
          <div
            key={name}
            className={cn(
              "grid gap-3 text-[13px]",
              limit === undefined
                ? "grid-cols-[minmax(0,7.5rem)_minmax(0,1fr)]"
                : "grid-cols-[minmax(0,5.5rem)_minmax(0,1fr)]",
            )}
          >
            <dt className="text-fg-3 truncate font-mono text-xs leading-5" title={fields[name]?.description}>
              {name}
            </dt>
            <dd className="min-w-0">
              {/* 限制行数时是预览，每个值也写得紧凑些。 */}
              <FieldValue value={content[name]} definition={fields[name]} compact={limit !== undefined} />
            </dd>
          </div>
        ))}
      </dl>
      {shown.length < names.length && (
        <p className="text-fg-3 mt-1.5 text-xs">还有 {names.length - shown.length} 个字段</p>
      )}
    </>
  );
}

/** 字段的值。compact 时数组只列前几项，长文字只写一两行。 */
function FieldValue({
  value,
  definition,
  compact,
}: {
  value: unknown;
  definition: FieldDefinition | undefined;
  compact?: boolean | undefined;
}) {
  if (value === null) return <span className="text-fg-3">null</span>;
  if (definition?.type === "reference" && typeof value === "string")
    return definition.target === "thing" ? (
      <Token thingId={value} size="sm" />
    ) : (
      <Token observationId={value} size="sm" />
    );
  if (Array.isArray(value)) {
    if (!value.length) return <span className="text-fg-3">空</span>;
    const shown = compact ? value.slice(0, COMPACT_ITEMS) : value;
    return (
      <ul className="grid gap-1">
        {shown.map((item, i) => (
          <li key={i} className="min-w-0">
            <FieldValue value={item} definition={definition?.items} compact={compact} />
          </li>
        ))}
        {shown.length < value.length && <li className="text-fg-3 text-xs">还有 {value.length - shown.length} 项</li>}
      </ul>
    );
  }
  if (typeof value === "object") {
    const record = value as Record<string, unknown>;
    // 标识线索这类 { scheme, value } 写成一行。
    if (typeof record.scheme === "string" && "value" in record)
      return (
        <span className="block min-w-0">
          <span className="text-fg-3 font-mono text-[11px]">{record.scheme}</span>
          <span className={cn("block font-mono text-xs", compact ? "truncate" : "break-all")}>
            {String(record.value)}
          </span>
        </span>
      );
    return (
      <dl className="grid gap-0.5">
        {Object.entries(record).map(([key, item]) => (
          <div key={key} className="flex min-w-0 gap-2">
            <dt className="text-fg-3 shrink-0 font-mono text-[11px]">{key}</dt>
            <dd className="min-w-0">
              <FieldValue value={item} definition={definition?.fields?.[key]} compact={compact} />
            </dd>
          </div>
        ))}
      </dl>
    );
  }
  if (typeof value === "boolean") return <span>{value ? "是" : "否"}</span>;
  return <span className={cn("break-words", compact && "line-clamp-2")}>{String(value)}</span>;
}

/** 面板底部默认收起：ID、原始内容和进入 Observation 页的入口。 */
function Raw({ item, role }: { item: Item; role: ItemInfo["role"] }) {
  const id = handleOf(item);
  return (
    <details className="group">
      <summary className="text-fg-3 hover:text-fg cursor-pointer text-xs select-none">ID 和原始数据</summary>
      <p className="text-fg-2 mt-2 font-mono text-xs break-all">{id}</p>
      <pre className="bg-panel mt-2 overflow-x-auto rounded-lg p-3 font-mono text-[11px] leading-relaxed">
        {JSON.stringify(item, null, 2)}
      </pre>
      <p className="text-fg-3 mt-2 flex gap-3 text-xs">
        {isObservation(item) && (
          <Link to="/observations/$observationId" params={{ observationId: id }} className="hover:text-fg">
            Observation 页 →
          </Link>
        )}
        {isThing(item) && (
          <Link to="/things/$thingId" params={{ thingId: id }} className="hover:text-fg">
            档案 →
          </Link>
        )}
        {role !== "definition" && role !== "evidence" && (
          <Link to="/records" search={{ root: id }} className="hover:text-fg">
            从这里织 →
          </Link>
        )}
      </p>
    </details>
  );
}
