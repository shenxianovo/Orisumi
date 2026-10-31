import { useState } from "react";
import { Token } from "@/components/token/Token";
import type { DaySummary } from "@/lib/day";
import { duration } from "@/lib/time";

const SHOWN = 12;

/** 这一天的应用：只算前台应用时段，同一个产品合成一行。 */
export function Applications({
  summary,
  details,
}: {
  summary: DaySummary;
  /** 同名的行写出它们不同的那一段。 */
  details: ReadonlyMap<string, string | null>;
}) {
  const [all, setAll] = useState(false);
  const rows = all ? summary.things : summary.things.slice(0, SHOWN);
  const longest = summary.things[0]?.total ?? 0;
  const total = summary.things.reduce((sum, row) => sum + row.total, 0);
  return (
    <section aria-label="应用" className="mt-10">
      <h2 className="flex flex-wrap items-baseline gap-x-2 text-[13px] font-medium">
        应用
        <span className="text-fg-3 text-xs font-normal tabular-nums">
          {duration(total)} · 在 {summary.things.length} 个应用之间切换了 {summary.switches} 次
        </span>
      </h2>
      <ol className="mt-2">
        {rows.map((row, i) => (
          <li
            key={row.key}
            className="border-line grid grid-cols-[20px_minmax(0,1fr)_minmax(0,1fr)_72px] items-center gap-2 border-t py-1.5 max-sm:grid-cols-[20px_minmax(0,1fr)_72px]"
          >
            <span className="text-fg-3 text-xs tabular-nums">{i + 1}</span>
            <span className="min-w-0">
              <Token item={row.thing} detail={details.get(row.key)} />
            </span>
            <span className="bg-panel h-1.5 overflow-hidden rounded-full max-sm:hidden">
              <span
                className="block h-full rounded-full"
                style={{ width: `${(row.total / longest) * 100}%`, background: row.color }}
              />
            </span>
            <span className="text-fg-2 text-right text-xs tabular-nums">{duration(row.total)}</span>
          </li>
        ))}
      </ol>
      {!all && summary.things.length > SHOWN && (
        <button
          type="button"
          onClick={() => setAll(true)}
          className="text-fg-3 hover:text-fg mt-1 cursor-pointer text-xs"
        >
          还有 {summary.things.length - SHOWN} 个
        </button>
      )}
    </section>
  );
}
