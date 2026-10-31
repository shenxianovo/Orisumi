import { useCallback, useEffect, useRef, useState } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import type { OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isInputSet, isObservation, isThing, type Item, type ObservationVersion, type Referrer } from "@/api/types";
import { RELATIONS, REVIEWS, type RelationContent } from "@/lib/common";
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
import { describeItem } from "./useEntityInfo";
import { pool, readThing, readVersion, schemaOf, useTypes, type TypeIndex } from "./useTypes";

interface Context {
  client: QueryClient;
  source: OrisumiSource;
  index: TypeIndex;
}

/** 引用一个实体的全部版本和引用位置。几十秒内重复展开时用缓存。 */
function readReferrers({ client, source }: Context, entityId: string): Promise<Referrer[]> {
  return client.fetchQuery({
    queryKey: ["entity", entityId, "referrers", "every"],
    staleTime: 30_000,
    queryFn: async () => {
      const found: Referrer[] = [];
      let after: string | undefined;
      do {
        const page = await source.listReferrers(entityId, { limit: 1000, ...(after ? { after } : {}) });
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
  reference.target === "thing" ? readThing(client, source, reference.id) : readVersion(client, source, reference.id);

const readId = (context: Context, versionId: string | null) =>
  versionId ? readVersion(context.client, context.source, versionId) : Promise.resolve(null);

/** 实体的类型名和 resourceName。 */
async function typeOf(context: Context, item: Item) {
  const schema = await schemaOf(context.client, context.source, context.index, item);
  return { typeName: describeItem(item, schema).typeName, resourceName: schema?.content.resourceName ?? null };
}

/** Observation 按事实时间，其余按保存时间；事物没有时间。只用来排序。 */
function timeOf(item: Item): number {
  if (isThing(item)) return 0;
  const recorded = parseTime(item.recordedAt) ?? 0;
  if (!isObservation(item)) return recorded;
  return parseTime(item.startAt) ?? parseTime(item.endAt) ?? recorded;
}

/** 一个实体在织图里画成什么：事物和经历是节点，陈述是结并带着它的 InputSet。InputSet 和定义不单独成节点。 */
async function resolve(context: Context, item: Item): Promise<Payload | null> {
  if (isThing(item)) return { kind: "entity", item };
  if (!isObservation(item)) return null;
  if (roleOf(item) === "experience") return { kind: "entity", item };
  const inputSet = await readId(context, item.inputSetVersionId);
  return { kind: "knot", observation: item, inputSet: inputSet && isInputSet(inputSet) ? inputSet : null };
}

/**
 * 一个节点的邻居，还没解析成节点。
 * - 事物：引用它的 Observation（左）；它写下的 Observation（右）；Relation 另一头的事物，线上写 predicate。
 * - 经历和结：记下它的 Observer（左，结的 Observer 由 toggleSource 长出）；输入（左）；业务字段引用的实体（右）；
 *   引用它的 Observation（左）；以它为输入的 Observation（右）。
 */
async function neighborsOf(context: Context, node: ItemNode) {
  const candidates: Omit<Neighbor, "typeName" | "time">[] = [];
  let unreadable = 0;
  const add = (item: Item | null, side: Side, label: string, via?: string) => {
    if (item) candidates.push({ item, side, label, ...(via ? { via } : {}) });
    else unreadable += 1;
  };

  async function incoming(entityId: string) {
    for (const { version, references } of await readReferrers(context, entityId)) {
      const paths = references.map((location) => location.path);
      if (isInputSet(version)) {
        // 以它为输入的 InputSet，再找到用这个 InputSet 的 Observation。
        for (const user of await readReferrers(context, version.entityId))
          if (isObservation(user.version)) add(user.version, "right", INPUT);
      } else if (isObservation(version)) {
        if (paths.includes("/observerId")) add(version, "right", SOURCE);
        else if ((await typeOf(context, version)).resourceName === RELATIONS) {
          const relation = version.content as unknown as RelationContent;
          const subject = relation.subjectId === entityId;
          const other = await readThing(
            context.client,
            context.source,
            subject ? relation.objectId : relation.subjectId,
          );
          add(other, subject ? "right" : "left", relation.predicate, version.entityId);
        } else if ((await typeOf(context, version)).resourceName === REVIEWS) add(version, "left", "复核");
        else add(version, "left", paths.map(referenceLabel).join("、"));
      }
    }
  }

  async function about(observation: ObservationVersion, withObserver: boolean) {
    const schema = await schemaOf(context.client, context.source, context.index, observation);
    for (const reference of outgoingReferences(observation, schema?.content.fields)) {
      if (reference.path === "observerVersionId" || reference.path === "inputSetVersionId") continue;
      if (reference.path === "observerId") {
        if (withObserver) add(await read(context, reference), "left", SOURCE);
        continue;
      }
      add(await read(context, reference), "right", referenceLabel(reference.path));
    }
    const inputSet = await readId(context, observation.inputSetVersionId);
    if (inputSet && isInputSet(inputSet)) {
      const members = await pool(inputSet.content.members, 8, (id) => readId(context, id));
      members.forEach((member) => add(member, "left", INPUT));
    }
    await incoming(observation.entityId);
  }

  const { payload } = node;
  if (payload.kind === "knot") await about(payload.observation, false);
  else if (isObservation(payload.item)) await about(payload.item, true);
  else await incoming(payload.item.entityId);

  const typed = await pool(candidates, 8, (candidate) => typeOf(context, candidate.item));
  const neighbors: Neighbor[] = [];
  candidates.forEach((candidate, i) => {
    if (roleOf(candidate.item) === "definition") return;
    neighbors.push({ ...candidate, typeName: typed[i]!.typeName, time: timeOf(candidate.item) });
  });
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
      const payload = await resolve(ctx, await source.getEntity(rootId));
      if (cancelled) return;
      if (!payload) {
        setError("InputSet 和定义跟着用到它们的观测出现，换一个实体作起点");
        return;
      }
      const weave = start(payload);
      setWeave(weave);
      setFresh(new Set(weave.nodes.map((node) => node.key)));
      setMessage("点一个节点，从两边长出和它有关的实体；再点一次收起");
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
            grown || bundled ? `长出 ${grown} 个` : "没有更多相关的实体",
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
