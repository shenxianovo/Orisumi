import { type Host, type Instance } from "@/api/management";

/** 采集的问题：离线的机器，以及启用的程序实例报告的错误。方法与托管页相同，同样的问题只写一次。 */
export function collectionProblems(hosts: readonly Host[], instances: readonly Instance[]): string[] {
  const live = new Map(hosts.filter((host) => !host.revoked).map((host) => [host.hostId, host]));
  const offline = [...live.values()].filter((host) => !host.connected).map((host) => `${host.name} 离线`);
  const failing = instances.flatMap((instance) => {
    if (!instance.enabled || instance.removed || !live.get(instance.hostId)?.connected) return [];
    const failed = instance.status?.state === "failed";
    if (!failed && !instance.status?.error) return [];
    return [`${instance.name}：${instance.status?.error ?? "运行失败"}`];
  });
  return [...new Set([...offline, ...failing])];
}
