import { useMemo } from "react";
import { Token } from "@/components/token/Token";
import { Why } from "@/components/token/Why";
import { Button } from "@/components/ui";
import type { NarrativeState } from "@/hooks/useEpisodes";
import { useVersion } from "@/hooks/queries";
import { groupThings, summarize, type Experience, type ThingGroup } from "@/lib/day";
import { streamedEpisode, type Episode } from "@/lib/episodes";
import type { SessionGroup } from "@/lib/conversations";
import { ago, clock, duration } from "@/lib/time";
import { cn } from "@/lib/utils";

/** 这一件事用到的应用和说过话的会话。 */
function Mentions({
  episode,
  periods,
  group,
  sessions,
}: {
  episode: Episode;
  periods: readonly Experience[];
  group: ThingGroup;
  sessions: ReadonlyMap<string, SessionGroup>;
}) {
  const applications = useMemo(() => {
    const ids = new Set(episode.localApplicationIds);
    const selected = periods.filter((period) => period.things.some((thing) => ids.has(thing.thing.entityId)));
    return summarize(groupThings(selected, group), episode.start, episode.end).things;
  }, [episode, periods, group]);
  const present = episode.sessionIds.flatMap((id) => sessions.get(id) ?? []);
  const spoken = present.filter((session) => session.userMessages > 0);
  const quiet = present.length - spoken.length;
  if (!applications.length && !present.length) return null;
  return (
    <div className="mt-2 flex flex-wrap items-center gap-x-1.5 gap-y-1">
      {applications.map((row) => (
        <Token key={row.key} item={row.thing} size="sm" detail={duration(row.total)} />
      ))}
      {spoken.map((session) => (
        <Token key={session.entityId} thingId={session.entityId} size="sm" />
      ))}
      {quiet > 0 && <span className="text-fg-3 text-xs">另有 {quiet} 个没有发言的会话</span>}
    </div>
  );
}

function EpisodeRow({
  episode,
  periods,
  group,
  sessions,
  hovered,
  onHover,
}: {
  episode: Episode;
  periods: readonly Experience[];
  group: ThingGroup;
  sessions: ReadonlyMap<string, SessionGroup>;
  hovered: boolean;
  onHover: (key: string | null) => void;
}) {
  return (
    <li
      onPointerEnter={() => onHover(episode.key)}
      onPointerLeave={() => onHover(null)}
      className={cn(
        "border-line -mx-3 grid grid-cols-[52px_minmax(0,1fr)] gap-4 rounded-xl border-t px-3 py-4 transition-colors",
        hovered && "bg-accent-soft",
      )}
    >
      <time className="text-fg-3 pt-0.5 text-xs leading-5 tabular-nums">
        {clock(episode.start)}
        <br />
        {clock(episode.end)}
      </time>
      <div className="min-w-0">
        <h3 className="text-[15px] leading-snug font-medium">{episode.title}</h3>
        <p className="text-fg-2 mt-1 text-[13px] leading-relaxed">{episode.summary}</p>
        <Mentions episode={episode} periods={periods} group={group} sessions={sessions} />
        {episode.version && (
          <p className="text-fg-3 mt-1.5 flex items-center gap-2 text-xs">
            <span className="border-infer w-4 border-t border-dashed" aria-hidden="true" />
            叙事 Observer · 推断
            <Why entityId={episode.version.entityId} />
          </p>
        )}
      </div>
    </li>
  );
}

/** 几件事由哪个模型、在什么时候写下；今天的记录之后又有新的保存时写出来，可以重写。 */
function Written({
  episodes,
  now,
  stale,
  busy,
  onRewrite,
}: {
  episodes: readonly Episode[];
  now: number;
  stale: boolean;
  busy: boolean;
  onRewrite: () => void;
}) {
  const first = episodes[0]?.version ?? null;
  const description = useVersion(first?.observerVersionId);
  const model = (description.data?.content as { model?: string } | undefined)?.model;
  const written = Math.max(...episodes.map((episode) => Date.parse(episode.version?.recordedAt ?? "") || 0));
  return (
    <p className="text-fg-3 mt-5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
      <span>
        {model ?? "叙事 Observer"} · {ago(now - written)}写下
      </span>
      {stale && <span>· 之后有新的记录</span>}
      <button
        type="button"
        disabled={busy}
        onClick={onRewrite}
        className="hover:text-fg cursor-pointer underline-offset-2 hover:underline disabled:cursor-default"
      >
        重写
      </button>
    </p>
  );
}

/** 这一天做了哪几件事。写着的时候逐件出现，保存后读取保存下来的。 */
export function Episodes({
  saved,
  state,
  periods,
  group,
  sessions,
  now,
  stale,
  hovered,
  onHover,
  onRewrite,
}: {
  saved: readonly Episode[];
  state: NarrativeState;
  periods: readonly Experience[];
  group: ThingGroup;
  sessions: ReadonlyMap<string, SessionGroup>;
  now: number;
  stale: boolean;
  hovered: string | null;
  onHover: (key: string | null) => void;
  onRewrite: () => void;
}) {
  const writing = state.kind === "writing" || state.kind === "waiting";
  const shown = state.kind === "writing" ? state.episodes.map(streamedEpisode) : state.kind === "waiting" ? [] : saved;
  return (
    <section aria-label="几件事">
      {saved.length > 0 && !writing && (
        <Written episodes={saved} now={now} stale={stale} busy={writing} onRewrite={onRewrite} />
      )}
      {state.kind === "failed" && (
        <p role="alert" className="text-danger mt-5 flex flex-wrap items-center gap-2 text-[13px]">
          这一天的几件事没有写成：{state.detail}
          <Button onClick={onRewrite}>重写</Button>
        </p>
      )}
      {state.kind === "unavailable" && <p className="text-fg-3 mt-5 text-[13px]">叙事 Observer 没有配置 API Key</p>}
      <ol className="mt-3">
        {shown.map((episode) => (
          <EpisodeRow
            key={episode.key}
            episode={episode}
            periods={periods}
            group={group}
            sessions={sessions}
            hovered={hovered === episode.key}
            onHover={onHover}
          />
        ))}
        {writing && (
          <li className="border-line text-fg-3 grid grid-cols-[52px_minmax(0,1fr)] gap-4 border-t py-4 text-[13px]">
            <span className="bg-infer beat mt-2 size-1.5 rounded-full" aria-hidden="true" />
            <span aria-live="polite">正在写</span>
          </li>
        )}
      </ol>
    </section>
  );
}
