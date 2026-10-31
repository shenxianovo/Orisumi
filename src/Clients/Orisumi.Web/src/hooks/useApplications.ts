import { useQuery } from "@tanstack/react-query";
import { ApiError, readAllPages, readAllThings, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import type { ListObservationsQuery, ObservationVersion, Thing } from "@/api/types";
import {
  buildCatalog,
  DESCRIPTIONS,
  IDENTIFIERS,
  LOCAL_APPLICATIONS,
  MEMBERSHIPS,
  PRODUCTS,
  type Catalog,
} from "@/lib/applications";
import type { ThingGroup } from "@/lib/day";

export const CATALOG_KEY = ["applications"] as const;

/** 资源名还没有登记时，后端返回 404，这时就是还没有这种数据。 */
async function orEmpty<T>(load: Promise<T[]>): Promise<T[]> {
  try {
    return await load;
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return [];
    throw error;
  }
}

function things(source: OrisumiSource, resourceName: string): Promise<Thing[]> {
  return orEmpty(
    readAllThings((after) => source.listThings({ resourceName, limit: 1000, ...(after ? { after } : {}) })),
  );
}

export function observations(source: OrisumiSource, query: ListObservationsQuery): Promise<ObservationVersion[]> {
  return orEmpty(
    readAllPages((after) => source.listObservations({ ...query, limit: 1000, ...(after ? { after } : {}) })),
  ) as Promise<ObservationVersion[]>;
}

/** 全部应用产品、当前理解中的线索归属、线索、本机应用和它们的描述。应用识别 Observer 在后台写入，所以定时重读。 */
export function useCatalog() {
  const source = useSource();
  return useQuery({
    queryKey: CATALOG_KEY,
    queryFn: async (): Promise<Catalog> => {
      const [products, memberships, clues, installs, descriptions] = await Promise.all([
        things(source, PRODUCTS),
        observations(source, { resourceName: MEMBERSHIPS, standing: "current" }),
        things(source, IDENTIFIERS),
        things(source, LOCAL_APPLICATIONS),
        observations(source, { resourceName: DESCRIPTIONS }),
      ]);
      return buildCatalog({
        products,
        memberships,
        clues: new Map(clues.map((clue) => [clue.entityId, clue])),
        installs,
        descriptions,
      });
    },
    staleTime: 10_000,
    refetchInterval: 30_000,
  });
}

/** 按产品合并：本机应用换成它所属的产品；未归属的本机应用保留，两者都算作“应用”。 */
export function productGroup(catalog: Catalog): ThingGroup {
  const installs = new Set(catalog.installs.map((install) => install.thing.entityId));
  return (entry) => {
    if (!installs.has(entry.thing.entityId)) return null;
    const product = catalog.productOf(entry.thing);
    return product ? { thing: product.thing, title: product.name, typeName: "应用" } : { ...entry, typeName: "应用" };
  };
}
