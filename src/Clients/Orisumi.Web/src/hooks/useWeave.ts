import { useCallback, useEffect, useRef, useState } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import type { OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { handleOf, isObservation, isThing, type Item, type Observation, type Referrer } from "@/api/types";
import { RELATIONS, REVIEWS, SAME_AS, type RelationContent, type SameAsContent } from "@/lib/common";
import { outgoingReferences, referenceLabel, type OutgoingReference } from "@/lib/references";
import { roleOf } from "@/lib/roles";
import { parseTime } from "@/lib/time";
import {
  collapse,
  grow,
  INPUT,
  link,
  linked,
  nextFromBundle,
  payloadKey,
  plan,
  REPLACE,
  REVEAL,
  reveal,
  SOURCE,
  start,
  unlink,
  type ItemNode,
  type Neighbor,
  type Payload,
  type Resolved,
  type Side,
  type Weave,
} from "@/lib/weave";
import { describeItem } from "./useItemInfo";
import { pool, readItem, readThing, readObservation, schemaOf, useTypes, type TypeIndex } from "./useTypes";

interface Context {
  client: QueryClient;
  source: OrisumiSource;
  index: TypeIndex;
}

/** 引用一项记录的全部 Observation 和引用位置。几十秒内重复展开时用缓存。 */
function readReferrers({ client, source }: Context, item: Item): Promise<Referrer[]> {
  const id = handleOf(item);
  return client.fetchQuery({
    queryKey: ["referrers", id, "every"],
    staleTime: 30_000,
    queryFn: async () => {
      const found: Referrer[] = [];
      let after: string | undefined;
      do {
        const query = { view: "all" as const, limit: 1000, ...(after ? { after } : {}) };
        const page = await (isThing(item)
          ? source.listThingReferrers(id, query)
          : source.listObservationReferrers(id, query));
        found.push(...page.referrers);
        after = page.next ?? undefined;
      } while (after);
      return found;
    },
  });
}

const read = (
  { client, source }: Context,
  reference: Pick<OutgoingReference, "target" | "id">,
): Promise<Item | null> =>
  reference.target === "thing"
    ? readThing(client, source, reference.id)
    : readObservation(client, source, reference.id);

const readInputSet = ({ client, source }: Context, inputSetId: string | null) =>
  inputSetId
    ? client.fetchQuery({
        queryKey: ["input-set", inputSetId],
        queryFn: () => source.getInputSet(inputSetId),
        staleTime: Infinity,
      })
    : Promise.resolve(null);

/** 记录的类型名和 resourceName。 */
async function typeOf(context: Context, item: Item) {
  const schema = isObservation(item)
    ? await schemaOf(context.client, context.source, item)
    : isThing(item)
      ? (context.index.bySchemaId.get(item.schemaId) ?? null)
      : null;
  return { typeName: describeItem(item, schema).typeName, resourceName: schema?.content.resourceName ?? null };
}

/** Observation 按事实时间，其余按保存时间；事物没有时间。只用来排序。 */
function timeOf(item: Item): number {
  if (!isObservation(item)) return 0;
  return parseTime(item.startAt) ?? parseTime(item.endAt) ?? parseTime(item.recordedAt) ?? 0;
}

/** 一项记录在织图里画成什么：事物和经历是节点，陈述是结并带着它的 InputSet。InputSet 和定义不单独成节点。 */
async function resolve(context: Context, item: Item): Promise<Payload | null> {
  if (isThing(item)) return { kind: "record", item };
  if (!isObservation(item)) return null;
  if (roleOf(item) === "experience") return { kind: "record", item };
  return { kind: "knot", observation: item, inputSet: await readInputSet(context, item.inputSetId) };
}

/**
 * 一个节点的邻居，还没解析成节点。
 * - 事物：引用它的 Observation（左）；它写下的 Observation（右）；Relation 和 Same-As 另一头的事物。
 * - 经历和结：记下它的 Observer（左，结的 Observer 由 toggleSource 长出）；输入（左）；业务字段引用的记录（右）；
 *   引用它的 Observation（左）；以它为输入的 Observation（右）；被它替换的（左）和替换它的（右）。
 */
async function neighborsOf(context: Context, node: ItemNode) {
  const candidates: Omit<Neighbor, "typeName" | "time">[] = [];
  let unreadable = 0;
  const add = (item: Item | null, side: Side, label: string, via?: string) => {
    if (item) candidates.push({ item, side, label, ...(via ? { via } : {}) });
    else unreadable += 1;
  };

  async function incoming(item: Item) {
    const id = handleOf(item);
    for (const { observation, references } of await readReferrers(context, item)) {
      const paths = references.map((location) => location.path);
      const { resourceName } = await typeOf(context, observation);
      if (paths.includes("/observerId")) add(observation, "right", SOURCE);
      else if (paths.includes("/inputSetId")) add(observation, "right", INPUT);
      else if (paths.some((path) => path.startsWith("/replaces/"))) add(observation, "right", REPLACE);
      else if (resourceName === RELATIONS) {
        const relation = observation.content as unknown as RelationContent;
        const subject = relation.subjectId === id;
        const other = await readThing(context.client, context.source, subject ? relation.objectId : relation.subjectId);
        add(other, subject ? "right" : "left", relation.predicate, observation.observationId);
      } else if (resourceName === SAME_AS) {
        const { leftId, rightId } = observation.content as unknown as SameAsContent;
        const other = await readThing(context.client, context.source, leftId === id ? rightId : leftId);
        add(other, "right", "同一", observation.observationId);
      } else if (resourceName === REVIEWS) add(observation, "left", "复核");
      else add(observation, "left", paths.map(referenceLabel).join("、"));
    }
  }

  async function about(observation: Observation, withObserver: boolean) {
    const schema = await schemaOf(context.client, context.source, observation);
    for (const reference of outgoingReferences(observation, schema?.content.fields)) {
      if (reference.path === "observerDescriptionId") continue;
      if (reference.path === "observerId") {
        if (withObserver) add(await read(context, reference), "left", SOURCE);
        continue;
      }
      if (reference.path.startsWith("replaces[")) add(await read(context, reference), "left", REPLACE);
      else add(await read(context, reference), "right", referenceLabel(reference.path));
    }
    const inputSet = await readInputSet(context, observation.inputSetId);
    if (inputSet) {
      const members = await pool(inputSet.members, 8, (id) => readObservation(context.client, context.source, id));
      members.forEach((member) => add(member, "left", INPUT));
    }
    await incoming(observation);
  }

  const { payload } = node;
  if (payload.kind === "knot") await about(payload.observation, false);
  else if (isObservation(payload.item)) await about(payload.item, true);
  else await incoming(payload.item);

  const typed = await pool(candidates, 8, (candidate) => typeOf(context, candidate.item));
  const neighbors: Neighbor[] = candidates.map((candidate, i) => ({
    ...candidate,
    typeName: typed[i]!.typeName,
    time: timeOf(candidate.item),
  }));
  return { neighbors, unreadable };
}

async function resolveAll(context: Context, neighbors: readonly Neighbor[]): Promise<Resolved[]> {
  const payloads = await pool(neighbors, 8, (neighbor) => resolve(context, neighbor.item));
  return neighbors.flatMap((neighbor, i) =>
    payloads[i]
      ? [
          {
            payload: payloads[i],
            side: neighbor.side,
            label: neighbor.label,
            ...(neighbor.via ? { via: neighbor.via } : {}),
          },
        ]
      : [],
  );
}

/**
 * 织图的状态：从 rootId 出发，toggle 展开或收起一个节点，open 从线束里再放出一些。
 * fresh 是刚长出来的节点；message 写最近一次做了什么。
 */
export function useWeave(rootId: string) {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  const [weave, setWeave] = useState<Weave | null>(null);
  const [fresh, setFresh] = useState<ReadonlySet<string>>(new Set());
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState("");
  const [error, setError] = useState<string | null>(null);

  // 异步展开时读最新的图和类型索引。类型索引定时刷新，刷新不重置已经长出来的图。
  const current = useRef<Weave | null>(null);
  const indexRef = useRef<TypeIndex | undefined>(undefined);
  useEffect(() => {
    current.current = weave;
    indexRef.current = types.data;
  });
  const context = useCallback((): Context | null => {
    const index = indexRef.current;
    return index ? { client, source, index } : null;
  }, [client, source]);

  // 起点换了时，画布按起点重建（调用方用 key），这里只读一次。
  const ready = !!types.data;
  useEffect(() => {
    if (!ready || !types.data) return;
    const ctx: Context = { client, source, index: types.data };
    let cancelled = false;
    (async () => {
      const item = await readItem(client, source, rootId);
      if (cancelled) return;
      const payload = item ? await resolve(ctx, item) : null;
      if (!payload) {
        setError(item ? "InputSet 跟着用到它的陈述出现，换一项记录作起点" : "读不到这份记录");
        return;
      }
      const weave = start(payload);
      setWeave(weave);
      setFresh(new Set(weave.nodes.map((node) => node.key)));
      setMessage("点一个节点，从两边长出和它有关的记录；再点一次收起");
    })().catch((failure: unknown) => {
      if (!cancelled) setError(failure instanceof Error ? failure.message : String(failure));
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rootId, ready, client, source]);

  function settle(before: Weave, next: Weave) {
    const keys = new Set(before.nodes.map((node) => node.key));
    const added = next.nodes.filter((node) => !keys.has(node.key));
    setWeave(next);
    setFresh(new Set(added.map((node) => node.key)));
    return added;
  }

  const toggle = useCallback(
    async (key: string) => {
      const now = current.current;
      const node = now?.nodes.find((item) => item.key === key);
      const ctx = context();
      if (!now || !node || node.kind === "bundle" || !ctx) return;
      if (node.expanded) {
        settle(now, collapse(now, key));
        setMessage("已收起");
        return;
      }
      setBusy(key);
      try {
        const { neighbors, unreadable } = await neighborsOf(ctx, node);
        const { direct, bundles } = plan(neighbors);
        const resolved = await resolveAll(ctx, direct);
        const latest = current.current!;
        const added = settle(latest, grow(latest, key, resolved, bundles));
        const grown = added.filter((item) => item.kind !== "bundle").length;
        const bundled = bundles.reduce((sum, bundle) => sum + bundle.members.length, 0);
        setMessage(
          [
            grown || bundled ? `长出 ${grown} 个` : "没有更多相关的记录",
            bundled ? `另有 ${bundled} 个收在线束里` : "",
            unreadable ? `${unreadable} 个引用的目标读不到` : "",
          ]
            .filter(Boolean)
            .join("，"),
        );
      } catch (failure) {
        setMessage(failure instanceof Error ? failure.message : String(failure));
      } finally {
        setBusy(null);
      }
    },
    [context],
  );

  const open = useCallback(
    async (key: string) => {
      const now = current.current;
      const ctx = context();
      const bundle = now?.nodes.find((item) => item.key === key);
      if (!now || !ctx || bundle?.kind !== "bundle") return;
      setBusy(key);
      try {
        const resolved = await resolveAll(ctx, nextFromBundle(now, key));
        const latest = current.current!;
        settle(latest, reveal(latest, key, resolved));
        const rest = Math.max(0, bundle.members.length - REVEAL);
        setMessage(rest > 0 ? `线束里还有 ${rest} 个` : "线束放完了");
      } catch (failure) {
        setMessage(failure instanceof Error ? failure.message : String(failure));
      } finally {
        setBusy(null);
      }
    },
    [context],
  );

  /** 从结上长出它的 Observer，已经连着时收起。 */
  const toggleSource = useCallback(
    async (key: string) => {
      const now = current.current;
      const node = now?.nodes.find((item) => item.key === key);
      const ctx = context();
      if (!now || !ctx || node?.kind !== "knot" || node.payload.kind !== "knot") return;
      setBusy(key);
      try {
        const observer = await readThing(ctx.client, ctx.source, node.payload.observation.observerId);
        const payload = observer ? await resolve(ctx, observer) : null;
        const latest = current.current!;
        if (!payload) setMessage("读不到这份陈述的 Observer");
        else if (node.parent === payloadKey(payload)) setMessage("这个结就是从它的来源长出来的");
        else if (linked(latest, key, payloadKey(payload))) {
          settle(latest, unlink(latest, key, payloadKey(payload)));
          setMessage("已收起来源");
        } else {
          settle(latest, link(latest, key, [{ payload, side: "left", label: SOURCE }]));
          setMessage("长出来源");
        }
      } catch (failure) {
        setMessage(failure instanceof Error ? failure.message : String(failure));
      } finally {
        setBusy(null);
      }
    },
    [context],
  );

  return {
    weave,
    fresh,
    busy,
    message,
    error: error ?? (types.error as Error | null)?.message ?? null,
    toggle,
    toggleSource,
    open,
  };
}
