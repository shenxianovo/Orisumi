import { useQuery } from "@tanstack/react-query";
import { readAllThings } from "@/api/client";
import { useSource } from "@/api/source";
import type { Thing } from "@/api/types";
import { useInstances } from "@/hooks/useManagement";
import type { Instance } from "@/api/management";
import { isUser } from "@/lib/common";
import { observerIdentity, type ObserverIdentity } from "@/lib/observers";
import { thingTitle } from "@/lib/titles";

export const OBSERVERS_INTERVAL = 60_000;

/** 一个 Observer：程序实例或用户本人，名称取实例登记的名称。 */
export interface ObserverInfo {
  thing: Thing;
  identity: ObserverIdentity;
}

export function describeObserver(thing: Thing, instance: Instance | undefined): ObserverInfo {
  return {
    thing,
    identity: isUser(thing)
      ? observerIdentity(thing.thingId, thing.name ?? "用户本人", "people")
      : observerIdentity(thing.thingId, instance?.name ?? thingTitle(thing, "Observer"), instance?.packageId ?? null),
  };
}

/** 全部 Observer：每个程序实例和用户本人。键是 thingId。 */
export function useObservers() {
  const source = useSource();
  const instances = useInstances();
  // 实例的状态每几秒刷新，名字和安装包变了才重读。
  const names = (instances.data ?? []).map(({ thingId, name, packageId }) => [thingId, name, packageId].join(":"));
  return useQuery({
    queryKey: ["observers", names.join()],
    queryFn: async () => {
      const [programs, people] = await Promise.all(
        ["program-instances", "people"].map((resourceName) =>
          readAllThings((after) => source.listThings({ resourceName, limit: 1000, ...(after ? { after } : {}) })),
        ),
      );
      const registered = new Map((instances.data ?? []).map((instance) => [instance.thingId, instance]));
      const infos = [...programs!, ...people!.filter(isUser)].map((thing) =>
        describeObserver(thing, registered.get(thing.thingId)),
      );
      return new Map(infos.map((info) => [info.thing.thingId, info]));
    },
    enabled: !instances.isPending,
    placeholderData: (previous) => previous,
    refetchInterval: OBSERVERS_INTERVAL,
  });
}
