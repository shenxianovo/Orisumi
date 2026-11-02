import { useMemo } from "react";
import { DayNav } from "@/components/day/DayNav";
import { Message, Segmented } from "@/components/ui";
import { useThing } from "@/hooks/queries";
import { useConversations } from "@/hooks/useConversations";
import { buildItems, productName, sessionSource, VIEWS, type SessionGroup, type View } from "@/lib/conversations";
import { Token } from "../token/Token";
import { Conversation } from "./Conversation";

/** 助手会话档案的下半部分：这一天的对话。 */
export function SessionDialog({
  sessionId,
  dateKey,
  view,
  onView,
}: {
  sessionId: string;
  dateKey: string;
  view: View;
  onView: (view: View) => void;
}) {
  const conversations = useConversations(dateKey);
  const group = conversations.groups?.find((candidate) => candidate.thingId === sessionId);
  const { range, today } = conversations;
  return (
    <section aria-label="对话" className="mt-10">
      <header className="border-line flex flex-wrap items-center gap-x-4 gap-y-2 border-b pb-3">
        <div className="min-w-[min(100%,280px)] flex-1">
          <DayNav start={range.start} today={today} small />
          {group && <SessionSource group={group} />}
        </div>
        <Segmented label="显示" options={VIEWS} value={view} onChange={onView} />
      </header>
      {conversations.error && !conversations.groups ? (
        <Message title="读不到这一天的对话" detail={(conversations.error as Error).message} />
      ) : !conversations.groups ? (
        <Message title="正在读取对话" />
      ) : !group ? (
        <Message title="这一天没有这个会话的交互" />
      ) : (
        <Items group={group} view={view} />
      )}
    </section>
  );
}

/** 会话的产品，以及交互中记下的客户端、工作目录和模型；子 Agent 的会话写出它的父会话。 */
function SessionSource({ group }: { group: SessionGroup }) {
  const session = group.session ? sessionSource(group.session) : null;
  const models = [...new Set(group.interactions.map((i) => i.observation.content.model).filter(Boolean))];
  const parent = useThing(group.parentThingId);
  return (
    <p className="text-fg-2 mt-1 text-xs break-all">
      {[productName(session?.product), group.client, group.workingDirectory, models.join("、")]
        .filter(Boolean)
        .join(" · ")}
      {parent.data && (
        <span className="ml-1 inline-flex items-center gap-1">
          · 子 Agent，父会话
          <Token item={parent.data} size="sm" />
        </span>
      )}
    </p>
  );
}

function Items({ group, view }: { group: SessionGroup; view: View }) {
  const items = useMemo(() => buildItems(group.interactions, view), [group.interactions, view]);
  const session = group.session ? sessionSource(group.session) : null;
  if (!items.length) return <Message title="这个视图下没有交互" detail="切到“过程”或“全部”看工具调用、推理和上下文" />;
  return (
    <div className="mt-3">
      <Conversation items={items} product={session?.product} />
    </div>
  );
}
