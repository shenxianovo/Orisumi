import type { NarrativeEpisode, Observation } from "@/api/types";
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
  observation: Observation | null;
}

export function episodeOf(observation: Observation): Episode {
  const content = observation.content as unknown as EpisodeContent;
  return {
    ...content,
    key: observation.observationId,
    start: parseTime(observation.startAt)!,
    end: parseTime(observation.endAt)!,
    observation,
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
    observation: null,
  };
}
