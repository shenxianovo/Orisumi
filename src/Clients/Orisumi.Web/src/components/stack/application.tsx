import type { ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import type { Observation, Thing } from "@/api/types";
import { Mono } from "@/components/ui";
import { useInputSet } from "@/hooks/queries";
import { useNow } from "@/hooks/useNow";
import { useCatalog } from "@/hooks/useApplications";
import { useReview } from "@/hooks/useReview";
import {
  clueOf,
  installDetails,
  MEMBERSHIPS,
  platformOf,
  type Catalog,
  type MembershipContent,
  type Product,
} from "@/lib/applications";
import { ago } from "@/lib/time";
import { cn } from "@/lib/utils";
import { Token } from "../token/Token";

function Section({ title, aside, children }: { title: string; aside?: ReactNode; children: ReactNode }) {
  return (
    <section>
      <div className="mb-1.5 flex items-baseline gap-2">
        <h3 className="text-fg-3 text-xs">{title}</h3>
        <span className="flex-1" />
        {aside && <span className="text-fg-3 text-xs">{aside}</span>}
      </div>
      {children}
    </section>
  );
}

/** 本机应用属于哪个产品，以及凭哪条线索。 */
export function Membership({ install }: { install: Thing }) {
  const catalog = useCatalog();
  if (!catalog.data) return null;
  const product = catalog.data.productOf(install);
  const hasClues = (catalog.data.installs.find((x) => x.thing.thingId === install.thingId)?.clues.length ?? 0) > 0;
  const clue = product?.memberships.find((m) =>
    catalog.data.installsOf(m.clue.thingId).some((x) => x.thing.thingId === install.thingId),
  )?.clue;
  return (
    <Section title="属于">
      {product ? (
        <p className="flex flex-wrap items-center gap-1.5 text-[13px]">
          <Token item={product.thing} />
          {clue && (
            <span className="text-fg-3">
              凭 <Mono>{clue.value}</Mono>
            </span>
          )}
        </p>
      ) : (
        <p className="text-fg-3 text-[13px]">
          {hasClues ? "未归属，等应用识别 Observer 认出它" : "没有跨设备线索，归不进产品"}
        </p>
      )}
    </Section>
  );
}

/** 产品面板：各平台的线索和归到它的安装，以及判给它的每一条判断。 */
export function ProductSections({ product }: { product: Thing }) {
  const catalog = useCatalog();
  const current = catalog.data?.products.find((p) => p.thing.thingId === product.thingId);
  return (
    <>
      {current && catalog.data && <Installs product={current} catalog={catalog.data} />}
      <Judgements product={product} />
    </>
  );
}

function Installs({ product, catalog }: { product: Product; catalog: Catalog }) {
  return (
    <Section title="线索与安装">
      <ul className="grid gap-2">
        {product.memberships.map(({ clue }) => {
          const list = catalog.installsOf(clue.thingId);
          const details = installDetails(list);
          return (
            <li key={clue.thingId}>
              <p className="flex items-baseline gap-2 text-[13px]">
                <span className="text-fg-2 w-14 shrink-0 text-xs">{platformOf(clue)}</span>
                <Mono className="min-w-0 truncate">{clue.value}</Mono>
              </p>
              <div className="mt-0.5 ml-16 flex flex-wrap gap-1">
                {list.length === 0 ? (
                  <span className="text-fg-3 text-xs">还没有出现在任何设备上</span>
                ) : (
                  list.map((install, i) => (
                    <Token key={install.thing.thingId} item={install.thing} size="sm" detail={details[i]} />
                  ))
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </Section>
  );
}

interface Judgement {
  observation: Observation;
  clue: Thing | null;
  /** 不在默认视图里时写明原因。 */
  status: "denied" | "replaced" | null;
}

/** 判给这个产品的全部线索归属。被否定和被替换的也列出，默认视图由后端算出。 */
function useJudgements(productId: string) {
  const source = useSource();
  return useQuery({
    queryKey: ["referrers", productId, "judgements"],
    queryFn: async (): Promise<Judgement[]> => {
      const query = { resourceName: MEMBERSHIPS, path: "/content/productId", limit: 1000 };
      const [every, current] = await Promise.all([
        source.listThingReferrers(productId, { ...query, view: "all" }),
        source.listThingReferrers(productId, query),
      ]);
      const live = new Set(current.referrers.map((r) => r.observation.observationId));
      const observations = every.referrers.map((r) => r.observation);
      const replaced = new Set(observations.flatMap((observation) => observation.replaces));
      const clues = await source.readThings([
        ...new Set(observations.map((v) => (v.content as unknown as MembershipContent).identifierId)),
      ]);
      const byId = new Map(clues.results.map((r) => [r.thingId, r.thing ?? null]));
      return observations.map((observation) => ({
        observation,
        clue: byId.get((observation.content as unknown as MembershipContent).identifierId) ?? null,
        status: live.has(observation.observationId)
          ? null
          : replaced.has(observation.observationId)
            ? "replaced"
            : "denied",
      }));
    },
    staleTime: 10_000,
  });
}

/** 判断记录：一条线索归属一行。判错时否定它，线索回到未归属。 */
function Judgements({ product }: { product: Thing }) {
  const judgements = useJudgements(product.thingId);
  const list = [...(judgements.data ?? [])].sort((a, b) =>
    b.observation.recordedAt.localeCompare(a.observation.recordedAt),
  );
  if (!list.length) return null;
  return (
    <Section title="判断记录" aside={`${list.length} 条`}>
      <ol className="border-line grid gap-3 border-l pl-3">
        {list.map((judgement) => (
          <JudgementRow key={judgement.observation.observationId} judgement={judgement} />
        ))}
      </ol>
    </Section>
  );
}

const STATUS = { denied: "已否定", replaced: "已被替换" } as const;

function InputCount({ inputSetId }: { inputSetId: string }) {
  const inputs = useInputSet(inputSetId);
  return <span>{inputs.data ? `${inputs.data.members.length} 个输入` : "…"}</span>;
}

function JudgementRow({ judgement }: { judgement: Judgement }) {
  const now = useNow(60_000);
  const review = useReview();
  const { observation, clue, status } = judgement;
  return (
    <li className="relative">
      <span
        className={cn(
          "absolute top-[7px] -left-[15.5px] size-[6px] rounded-full",
          status ? "border-fg-3 bg-card border" : "bg-fg-3",
        )}
      />
      <p className="flex items-baseline gap-2 text-[13px] leading-snug">
        <span className={cn("min-w-0 flex-1 break-all", status === "denied" && "text-fg-3 line-through")}>
          {clue ? clueOf(clue).value : "读不到的线索"}
        </span>
        {status ? (
          <span className="text-fg-3 shrink-0 text-xs">{STATUS[status]}</span>
        ) : (
          <button
            type="button"
            disabled={review.isPending}
            onClick={() => review.mutate({ targetId: observation.observationId, verdict: "deny" })}
            className="text-fg-3 hover:text-fg shrink-0 cursor-pointer text-xs disabled:cursor-default"
          >
            {review.isPending ? "正在否定" : "否定"}
          </button>
        )}
      </p>
      <p className="text-fg-3 mt-1 flex flex-wrap items-center gap-1 text-xs">
        <Token thingId={observation.observerId} size="sm" />
        <span>{ago(now - Date.parse(observation.recordedAt))}</span>
        {observation.inputSetId && (
          <>
            <span>· 依据</span>
            <InputCount inputSetId={observation.inputSetId} />
          </>
        )}
      </p>
      {review.error && <p className="text-danger mt-1 text-xs">{(review.error as Error).message}</p>}
    </li>
  );
}
