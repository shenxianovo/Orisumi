import type { RecognitionKey, Thing } from "@/api/types";

// Core 内置的公共类型，见 Docs core/rules/understanding.mdx#common-types。

export const PERSON_SCHEMA = "019e1bbb-ec00-7000-8000-000000000006";
export const PROGRAM_SCHEMA = "019e1bbb-ec00-7000-8000-000000000012";
export const REVIEW_SCHEMA = "019e1bbb-ec00-7000-8000-00000000000e";
export const RELATION_SCHEMA = "019e1bbb-ec00-7000-8000-00000000000f";
export const NAME_SCHEMA = "019e1bbb-ec00-7000-8000-000000000010";

export const REVIEWS = "reviews";
export const RELATIONS = "relations";
export const NAMES = "names";

/** 用户本人的识别依据，见 Docs observers/user/index.mdx#identity。 */
export const USER_KEY: RecognitionKey = { scope: null, scheme: "orisumi.user", value: "owner" };

export function isUser(thing: Thing): boolean {
  return (
    thing.schemaId === PERSON_SCHEMA &&
    thing.keys.some((key) => key.scheme === USER_KEY.scheme && key.value === USER_KEY.value)
  );
}

export type Verdict = "confirm" | "deny" | "supersede";

export interface ReviewContent {
  targetVersionId: string;
  verdict: Verdict;
  text: string | null;
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
