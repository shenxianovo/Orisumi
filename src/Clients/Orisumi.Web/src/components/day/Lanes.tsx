import type { ReactNode } from "react";
import { clip, type Row } from "@/lib/day";
import type { Episode } from "@/lib/episodes";
import { clock } from "@/lib/time";
import { cn } from "@/lib/utils";

const position = (at: number, from: number, to: number) => `${((at - from) / (to - from)) * 100}%`;

function Lane({ label, children, thin }: { label: string; children: ReactNode; thin?: boolean }) {
  return (
    <div className="grid grid-cols-[52px_minmax(0,1fr)] items-center gap-2 max-sm:grid-cols-1 max-sm:gap-0">
      <span className="text-fg-3 text-[11px]">{label}</span>
      <div className={cn("bg-panel relative overflow-hidden rounded", thin ? "h-2.5" : "h-5")}>{children}</div>
    </div>
  );
}

/** 一天的条：0 点到 24 点，三道分别是几件事的时段、用户说话的时刻和前台应用时段。今天有一道现在的竖线。 */
export function Lanes({
  episodes,
  said,
  applications,
  from,
  to,
  now,
  hovered,
  onHover,
}: {
  episodes: readonly Episode[];
  said: readonly number[];
  applications: readonly Row[];
  from: number;
  to: number;
  /** 今天的现在；其他日期为 null。 */
  now: number | null;
  hovered: string | null;
  onHover: (key: string | null) => void;
}) {
  const span = (start: number, end: number) => {
    const range = clip(start, end, from, to);
    if (!range) return null;
    return {
      left: position(range[0], from, to),
      width: `max(2px, ${((range[1] - range[0]) / (to - from)) * 100}%)`,
    };
  };
  return (
    <div aria-label="一天的条" className="relative grid gap-1.5">
      <div className="text-fg-3 grid grid-cols-[52px_minmax(0,1fr)] gap-2 text-[10px] tabular-nums max-sm:grid-cols-1">
        <span className="max-sm:hidden" />
        <div className="flex justify-between">
          {[0, 6, 12, 18, 24].map((hour) => (
            <span key={hour}>{hour}</span>
          ))}
        </div>
      </div>
      <Lane label="几件事">
        {episodes.map((episode) => {
          const style = span(episode.start, episode.end);
          if (!style) return null;
          return (
            <span
              key={episode.key}
              title={`${clock(episode.start)}–${clock(episode.end)} ${episode.title}`}
              onPointerEnter={() => onHover(episode.key)}
              onPointerLeave={() => onHover(null)}
              className={cn(
                "bg-infer absolute inset-y-0.5 rounded-sm transition-opacity",
                hovered === episode.key ? "opacity-100" : "opacity-45",
              )}
              style={style}
            />
          );
        })}
      </Lane>
      <Lane label="你说的话" thin>
        {said.map((at, i) => (
          <span key={i} className="bg-fg-2 absolute inset-y-0 w-px" style={{ left: position(at, from, to) }} />
        ))}
      </Lane>
      <Lane label="前台" thin>
        {applications.flatMap((row) =>
          row.segments.map((segment) => (
            <span
              key={segment.observationId}
              title={`${row.title} ${clock(segment.start)}–${clock(segment.end)}`}
              className="absolute inset-y-0"
              style={{ ...span(segment.start, segment.end), background: row.color }}
            />
          )),
        )}
      </Lane>
      {now !== null && (
        <div
          aria-hidden="true"
          className="pointer-events-none absolute inset-y-0 right-0 left-[60px] max-sm:left-0"
          title="现在"
        >
          <span className="bg-danger absolute inset-y-0 w-px" style={{ left: position(now, from, to) }} />
        </div>
      )}
    </div>
  );
}
