import { useMemo, useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { Applications } from "@/components/day/Applications";
import { DayNav } from "@/components/day/DayNav";
import { Episodes } from "@/components/day/Episodes";
import { Lanes } from "@/components/day/Lanes";
import { Message } from "@/components/ui";
import { productGroup, useCatalog } from "@/hooks/useApplications";
import { useCollectionProblems } from "@/hooks/useCollection";
import { useConversations } from "@/hooks/useConversations";
import { useDay } from "@/hooks/useDay";
import { useEpisodes, useNarrative } from "@/hooks/useEpisodes";
import { useNewJudgments } from "@/hooks/useJudgments";
import { useNow } from "@/hooks/useNow";
import type { SessionGroup } from "@/lib/conversations";
import { groupThings, PERIODS, summarize, type Row, type ThingGroup } from "@/lib/day";
import { episodeOf, streamedEpisode } from "@/lib/episodes";
import { differences } from "@/lib/roles";
import { dayOf, dayRange, duration } from "@/lib/time";

interface DaySearch {
  date?: string | undefined;
}

export const Route = createFileRoute("/")({
  validateSearch: (search: Record<string, unknown>): DaySearch =>
    typeof search.date === "string" && dayOf(search.date) ? { date: search.date } : {},
  component: DayPage,
});

const SAME: ThingGroup = () => null;

/** 今天：这一天做了哪几件事，下面是一天的条和用过的应用。 */
function DayPage() {
  const search = Route.useSearch();
  const now = useNow(1000);
  const key = search.date ?? dayRange(now).key;
  const day = useDay(key);
  const conversations = useConversations(key);
  const catalog = useCatalog();
  const { range, today } = day;
  const episodes = useEpisodes(range, today);
  const [hovered, setHovered] = useState<string | null>(null);

  const experiences = day.data?.experiences;
  const periods = useMemo(() => (experiences ?? []).filter((e) => e.contentType === PERIODS), [experiences]);
  const group = useMemo(() => (catalog.data ? productGroup(catalog.data) : SAME), [catalog.data]);
  const summary = useMemo(
    () => summarize(groupThings(periods, group), range.start, range.end),
    [periods, group, range.start, range.end],
  );
  const details = useMemo(() => sameNameDetails(summary.things), [summary.things]);

  const groups = conversations.groups;
  const sessions = useMemo(
    () => new Map<string, SessionGroup>((groups ?? []).map((session) => [session.thingId, session])),
    [groups],
  );
  const said = useMemo(
    () =>
      (groups ?? []).flatMap((session) =>
        session.interactions
          .filter(({ observation: { content } }) => content.kind === "message" && content.actor === "user")
          .map((interaction) => interaction.at),
      ),
    [groups],
  );

  const hasRecords = periods.length > 0 || sessions.size > 0;
  const loaded = !!day.data && !!groups;
  const narrative = useNarrative(range, episodes, loaded && hasRecords);
  const saved = useMemo(() => (episodes.data ?? []).map(episodeOf), [episodes.data]);
  const lanes = narrative.state.kind === "writing" ? narrative.state.episodes.map(streamedEpisode) : saved;

  const written = Math.max(0, ...saved.map((episode) => Date.parse(episode.observation!.recordedAt)));
  const stale = today && saved.length > 0 && (experiences ?? []).some((e) => e.writtenAt > written);
  const error = day.error ?? conversations.error ?? episodes.error;

  return (
    <section>
      <header>
        <DayNav start={range.start} today={today} />
        {hasRecords && (
          <p className="text-fg-2 mt-1.5 text-[13px] tabular-nums">
            {[
              lanes.length > 0 && `${lanes.length} 件事`,
              sessions.size > 0 && `${sessions.size} 个助手会话，你说了 ${said.length} 句`,
              summary.things.length > 0 &&
                `前台记录 ${duration(summary.things.reduce((sum, row) => sum + row.total, 0))}`,
            ]
              .filter(Boolean)
              .join(" · ")}
          </p>
        )}
        <Signals />
      </header>

      {error && !loaded ? (
        <Message title="读不到这一天" detail={(error as Error).message} mascot />
      ) : !loaded ? (
        <Message title="正在读取这一天" />
      ) : !hasRecords && !saved.length ? (
        <Message title={today ? "今天还没有记录" : "这一天没有记录"} mascot />
      ) : (
        <div className="rise mt-6">
          <Lanes
            episodes={lanes}
            said={said}
            applications={summary.things}
            from={range.start}
            to={range.end}
            now={today ? now : null}
            hovered={hovered}
            onHover={setHovered}
          />
          <Episodes
            saved={saved}
            state={narrative.state}
            periods={periods}
            group={group}
            sessions={sessions}
            now={now}
            stale={stale}
            hovered={hovered}
            onHover={setHovered}
            onRewrite={() => void narrative.write()}
          />
          {summary.things.length > 0 && <Applications summary={summary} details={details} />}
        </div>
      )}
    </section>
  );
}

/** 页头的提示，只在需要时出现：采集出了问题，以及模型新认定了几件。 */
function Signals() {
  const problems = useCollectionProblems();
  const fresh = useNewJudgments();
  if (!problems.length && !fresh) return null;
  return (
    <div className="mt-2 grid justify-items-start gap-0.5 text-[13px]">
      {problems.length > 0 && (
        <Link to="/hosts" className="text-danger flex items-center gap-2 hover:underline">
          <span className="bg-danger size-1.5 rounded-full" aria-hidden="true" />
          {problems.join("；")}
        </Link>
      )}
      {fresh > 0 && (
        <Link to="/understanding" className="text-fg-2 hover:text-fg">
          模型新认定 {fresh} 件 →
        </Link>
      )}
    </div>
  );
}

/** 同名的事物（例如同一个 bundle id 的几个安装）后面写出它们不同的那一段。 */
function sameNameDetails(rows: Row[]): Map<string, string | null> {
  const groups = new Map<string, Row[]>();
  for (const row of rows) groups.set(row.title, [...(groups.get(row.title) ?? []), row]);
  const out = new Map<string, string | null>();
  for (const group of groups.values()) {
    if (group.length < 2) continue;
    const found = differences(group.map((row) => row.thing.keys));
    group.forEach((row, i) => out.set(row.key, found[i] ?? null));
  }
  return out;
}
