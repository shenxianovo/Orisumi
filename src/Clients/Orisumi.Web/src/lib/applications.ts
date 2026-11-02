import type { Observation, Thing } from "@/api/types";
import { distinctSegment } from "./roles";

// 应用产品和本机应用的归属：本机应用描述里的跨设备线索在当前理解中有线索归属时，它属于线索归属指向的产品。
// 规则见 Docs observers/application-identity/index.mdx#membership。

export const PRODUCTS = "application-products";
export const MEMBERSHIPS = "application-memberships";
export const IDENTIFIERS = "application-identifiers";
export const LOCAL_APPLICATIONS = "local-applications";
export const DESCRIPTIONS = "local-application-descriptions";

export interface Identifier {
  scheme: string;
  value: string;
}

/** 一条线索：线索这个事物，以及它的识别依据。 */
export interface Clue extends Identifier {
  thingId: string;
}

/** 能跨设备复用的 scheme，见 Local Application · 标识线索。 */
const CROSS_DEVICE = new Set(["macos.bundle-id", "android.package-name"]);

const PLATFORMS: Record<string, string> = { macos: "macOS", android: "Android", windows: "Windows", linux: "Linux" };

export function isCrossDevice(identifier: Identifier): boolean {
  return CROSS_DEVICE.has(identifier.scheme);
}

export function platformOf(identifier: Identifier): string {
  const prefix = identifier.scheme.split(".")[0] ?? identifier.scheme;
  return PLATFORMS[prefix] ?? prefix;
}

export function clueOf(thing: Thing): Clue {
  const key = thing.keys[0];
  return { thingId: thing.thingId, scheme: key?.scheme ?? "", value: key?.value ?? thing.thingId };
}

export interface MembershipContent {
  identifierId: string;
  productId: string;
}

export interface DescriptionContent {
  localApplicationId: string;
  deviceId: string;
  identifierIds: string[];
  executablePath: string | null;
}

export interface Membership {
  observation: Observation;
  clue: Clue;
}

export interface Product {
  thing: Thing;
  name: string;
  memberships: Membership[];
}

export interface Install {
  thing: Thing;
  /** 默认视图中的本机应用描述。 */
  description: Observation | null;
  /** 跨设备线索，按它们归属。 */
  clues: Clue[];
  executablePath: string | null;
}

export interface Catalog {
  /** 有线索归属的产品。 */
  products: Product[];
  installs: Install[];
  /** 这条线索当前属于哪个产品。 */
  ownerOf(clueId: string): Product | null;
  /** 本机应用属于哪个产品。 */
  productOf(install: Thing): Product | null;
  /** 拥有这条线索的本机应用。 */
  installsOf(clueId: string): Install[];
}

export interface CatalogInput {
  products: readonly Thing[];
  /** 当前理解中的线索归属。 */
  memberships: readonly Observation[];
  clues: ReadonlyMap<string, Thing>;
  installs: readonly Thing[];
  descriptions: readonly Observation[];
}

export function buildCatalog(input: CatalogInput): Catalog {
  const products = new Map<string, Product>(
    input.products.map((thing) => [
      thing.thingId,
      { thing, name: thing.name ?? thing.keys[0]?.value ?? "应用产品", memberships: [] },
    ]),
  );
  const owners = new Map<string, Product>();
  for (const observation of input.memberships) {
    const content = observation.content as unknown as MembershipContent;
    const product = products.get(content.productId);
    const clue = input.clues.get(content.identifierId);
    // 指向的产品或线索已删除时，这条线索未归属。
    if (!product || !clue) continue;
    product.memberships.push({ observation, clue: clueOf(clue) });
    owners.set(clue.thingId, product);
  }
  // 本机应用描述是状态，默认视图里每个本机应用只有一份。
  const latest = new Map<string, Observation>(
    input.descriptions.map((description) => [
      (description.content as unknown as DescriptionContent).localApplicationId,
      description,
    ]),
  );
  const installs = input.installs.map((thing): Install => {
    const description = latest.get(thing.thingId) ?? null;
    const content = description?.content as unknown as DescriptionContent | undefined;
    const clues = (content?.identifierIds ?? []).flatMap((id) => {
      const clue = input.clues.get(id);
      return clue ? [clueOf(clue)] : [];
    });
    return { thing, description, clues: clues.filter(isCrossDevice), executablePath: content?.executablePath ?? null };
  });
  const byClue = new Map<string, Install[]>();
  const byEntity = new Map<string, Install>();
  for (const install of installs) {
    byEntity.set(install.thing.thingId, install);
    for (const clue of install.clues) byClue.set(clue.thingId, [...(byClue.get(clue.thingId) ?? []), install]);
  }

  const ownerOf = (clueId: string) => owners.get(clueId) ?? null;
  return {
    products: [...products.values()].filter((product) => product.memberships.length > 0),
    installs,
    ownerOf,
    productOf: (thing) =>
      (byEntity.get(thing.thingId)?.clues ?? []).map((clue) => ownerOf(clue.thingId)).find((p) => p !== null) ?? null,
    installsOf: (clueId) => byClue.get(clueId) ?? [],
  };
}

/** 同一条线索的几个安装，各自路径里不同的那一段，例如 `…/Release/…` 和 `…/Debug/…`；只有一个时为 null。 */
export function installDetails(installs: readonly Install[]): (string | null)[] {
  const paths = installs.map((install) => install.executablePath ?? "");
  return paths.map((path) => (path ? distinctSegment(path, paths) : null));
}
