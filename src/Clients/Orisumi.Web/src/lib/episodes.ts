import type { NarrativeEpisode, ObservationVersion } from "@/api/types";
import { parseTime } from "./time";

// 一天中的一件事，见 Docs observers/narrative/episode.mdx。写着的时候来自叙事流，保存后来自 Observation。

export const EPISODES = "episodes";

export interface EpisodeContent {
  title: string;
  summary: string;
  sessionIds: string[];
  localApplicationIds: string[];
}

export interface Episode extends EpisodeContent {
  key: string;
  start: number;
  end: number;
  /** 还没有保存时为 null。 */
  version: ObservationVersion | null;
}

export function episodeOf(version: ObservationVersion): Episode {
  const content = version.content as unknown as EpisodeContent;
  return {
    ...content,
    key: version.entityId,
    start: parseTime(version.startAt)!,
    end: parseTime(version.endAt)!,
    version,
  };
}

export function streamedEpisode(episode: NarrativeEpisode, index: number): Episode {
  const { title, summary, sessionIds, localApplicationIds } = episode;
  return {
    title,
    summary,
    sessionIds,
    localApplicationIds,
    key: `streamed-${index}`,
    start: Date.parse(episode.startAt),
    end: Date.parse(episode.endAt),
    version: null,
  };
}
