import { useMemo } from "react";
import { collectionProblems } from "@/lib/collection";
import { useHosts, useInstances } from "./useManagement";

/** 现在采集出了什么问题；侧栏和今天的页头共用。 */
export function useCollectionProblems(): string[] {
  const hosts = useHosts();
  const instances = useInstances();
  return useMemo(() => collectionProblems(hosts.data ?? [], instances.data ?? []), [hosts.data, instances.data]);
}
