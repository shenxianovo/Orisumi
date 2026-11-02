import { useQuery } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import type { Observation } from "@/api/types";
import { observations } from "./useApplications";

export interface HistoryEntry {
  observation: Observation;
  /** 这份说法现在的位置，写给人看；隐藏与否由后端的默认视图决定。 */
  status: string;
}

/**
 * 关于一个事物的全部说法，新的在前。默认视图里有的是现在采用的；其余的按它引用方的事实写明为什么不在：
 * 有人替换了它，或者有生效的否定指向它。
 */
export function useHistory(thingId: string) {
  const source = useSource();
  return useQuery({
    queryKey: ["history", thingId],
    queryFn: async (): Promise<HistoryEntry[]> => {
      const [all, current] = await Promise.all([
        observations(source, { thingId, role: "about", view: "all", order: "desc" }),
        observations(source, { thingId, role: "about" }),
      ]);
      const live = new Set(current.map((observation) => observation.observationId));
      const replaced = new Set(all.flatMap((observation) => observation.replaces));
      return all
        .sort((a, b) => b.recordedAt.localeCompare(a.recordedAt))
        .map((observation) => ({
          observation,
          status: live.has(observation.observationId)
            ? "现在采用"
            : replaced.has(observation.observationId)
              ? "已被替换"
              : "没有采用",
        }));
    },
    staleTime: 10_000,
  });
}
