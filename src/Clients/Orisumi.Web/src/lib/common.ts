import type { RecognitionKey, Thing } from "@/api/types";

// Core 内置的类型，见 Docs core/model/common-types.mdx。

export const PERSON_SCHEMA = "019e1bbb-ec00-7000-8000-000000000006";
export const PROGRAM_SCHEMA = "019e1bbb-ec00-7000-8000-000000000012";
export const REVIEW_SCHEMA = "019e1bbb-ec00-7000-8000-00000000000e";
export const RELATION_SCHEMA = "019e1bbb-ec00-7000-8000-00000000000f";
export const NAME_SCHEMA = "019e1bbb-ec00-7000-8000-000000000010";
export const SAME_AS_SCHEMA = "019e1bbb-ec00-7000-8000-00000000001a";
export const ADOPTION_SCHEMA = "019e1bbb-ec00-7000-8000-00000000001b";
export const OBSERVER_DESCRIPTION_SCHEMA = "019e1bbb-ec00-7000-8000-00000000001c";

export const REVIEWS = "reviews";
export const RELATIONS = "relations";
export const NAMES = "names";
export const SAME_AS = "same-as";
export const OBSERVER_DESCRIPTIONS = "observer-descriptions";

/** 用户本人的识别依据，见 Docs observers/user/index.mdx#identity。 */
export const USER_KEY: RecognitionKey = { scope: null, scheme: "orisumi.user", value: "owner" };

export function isUser(thing: Thing): boolean {
  return (
    thing.schemaId === PERSON_SCHEMA &&
    thing.keys.some((key) => key.scheme === USER_KEY.scheme && key.value === USER_KEY.value)
  );
}

export type Verdict = "confirm" | "deny";

export interface ReviewContent {
  verdict: Verdict;
  /** 被复核的 Observation；否定一个范围时为 null。 */
  targetId: string | null;
  scope: { thingIds: string[]; schemaIds: string[] } | null;
  text: string | null;
}

export interface SameAsContent {
  leftId: string;
  rightId: string;
}

export interface RelationContent {
  subjectId: string;
  predicate: string;
  objectId: string;
  text: string | null;
}

/** 形成方式在界面上的写法，见 Docs core/model/observation.mdx#formation。 */
export const FORMATION_NAMES: Record<string, string> = {
  direct: "直接读到",
  stated: "亲口陈述",
  computed: "确定计算",
  extracted: "模型提取",
  inferred: "推断",
};
