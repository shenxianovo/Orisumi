import { useQueries, useQuery } from "@tanstack/react-query";
import { isUnreadable, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isBinaryValue, type BinaryValue, type Thing } from "@/api/types";
import { LOCAL_APPLICATIONS, PRODUCTS } from "@/lib/applications";
import { useContentUrl } from "./queries";
import { useCatalog } from "./useApplications";

/** 从最近的这么多段引用方里找图标；更早的时段多半在图标采集之前，不再往前翻。 */
const RECENT = 20;

interface FoundIcon {
  /** 带着图标的那一段时段，observationId 越大越新。 */
  observationId: string;
  icon: BinaryValue;
}

async function latestIcon(source: OrisumiSource, thingId: string): Promise<FoundIcon | null> {
  try {
    const page = await source.listThingReferrers(thingId, {
      path: "/content/localApplicationId",
      order: "desc",
      limit: RECENT,
    });
    for (const { observation } of page.referrers) {
      const icon = (observation.content as Record<string, unknown> | null)?.icon;
      if (isBinaryValue(icon)) return { observationId: observation.observationId, icon };
    }
    return null;
  } catch (error) {
    if (isUnreadable(error)) return null;
    throw error;
  }
}

const iconQuery = (source: OrisumiSource, thingId: string | null) => ({
  queryKey: ["icon", thingId],
  queryFn: () => latestIcon(source, thingId!),
  enabled: !!thingId,
  staleTime: 60_000,
});

/**
 * 本机应用和应用产品自己的图标，见 Web 文档的“角色和记号”。本机应用取提到它的前台时段中最近带图标的那一段；
 * 应用产品取它的本机应用里最新的那个。其他记录和还没有图标的应用返回 undefined，记号用类型图标。
 */
export function useThingImage(thing: Thing | null | undefined, resourceName: string | null | undefined) {
  const source = useSource();
  const catalog = useCatalog();
  const install = resourceName === LOCAL_APPLICATIONS ? (thing?.thingId ?? null) : null;
  const product = resourceName === PRODUCTS && thing ? thing.thingId : null;
  const installs =
    product && catalog.data
      ? catalog.data.installs
          .filter((i) => catalog.data.productOf(i.thing)?.thing.thingId === product)
          .map((i) => i.thing.thingId)
      : [];
  const own = useQuery(iconQuery(source, install));
  const members = useQueries({ queries: installs.map((id) => iconQuery(source, id)) });
  const newest = members
    .map((member) => member.data)
    .reduce<FoundIcon | null>(
      (best, found) => (found && (!best || found.observationId > best.observationId) ? found : best),
      null,
    );
  const icon = install ? own.data?.icon : newest?.icon;
  return useContentUrl(icon ?? null).data;
}
