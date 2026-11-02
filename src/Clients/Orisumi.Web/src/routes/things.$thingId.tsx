import { useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useSource } from "@/api/source";
import type { DeleteResult, Thing } from "@/api/types";
import { Archive } from "@/components/archive/Archive";
import { DeleteDialog } from "@/components/record/Panels";
import { StatementRow } from "@/components/record/statement";
import { SessionDialog } from "@/components/sessions/SessionDialog";
import { Token } from "@/components/token/Token";
import { Button, Card, Message, SectionTitle } from "@/components/ui";
import { refreshView, useThing } from "@/hooks/queries";
import { useArchive } from "@/hooks/useArchive";
import { useHistory } from "@/hooks/useHistory";
import { useNow } from "@/hooks/useNow";
import { useTypes } from "@/hooks/useTypes";
import { VIEWS, type View } from "@/lib/conversations";
import { dayOf, dayRange, parseTime } from "@/lib/time";

const SESSIONS = "assistant-sessions";
const INTERACTIONS = "assistant-interactions";

interface ThingSearch {
  date?: string | undefined;
  view?: View | undefined;
}

export const Route = createFileRoute("/things/$thingId")({
  validateSearch: (search: Record<string, unknown>): ThingSearch => ({
    ...(typeof search.date === "string" && dayOf(search.date) ? { date: search.date } : {}),
    ...(VIEWS.some((v) => v.value === search.view) ? { view: search.view as View } : {}),
  }),
  component: ThingPage,
});

/** 一个事物的档案：现在的样子、关系和经历，下面是历史；助手会话再显示这一天的对话。 */
function ThingPage() {
  const { thingId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const thing = useThing(thingId);
  const types = useTypes();
  const archive = useArchive(thingId);
  const now = useNow(60_000);
  const [deleted, setDeleted] = useState<DeleteResult | null>(null);

  if (deleted) return <Deleted result={deleted} />;
  if (thing.isPending) return <Message title="正在读取这个事物" />;
  if (!thing.data)
    return (
      <Message title="读不到这个事物" detail={thingId} mascot>
        <Link to="/things" className="text-fg-2 hover:text-fg text-[13px] underline underline-offset-4">
          回到事物
        </Link>
      </Message>
    );

  const type = types.data?.bySchemaId.get(thing.data.schemaId)?.content;
  const latest = archive.data?.latest.get(INTERACTIONS);
  const latestAt = parseTime(latest?.endAt ?? latest?.startAt);
  return (
    <section>
      <nav aria-label="位置" className="text-fg-3 mb-5 flex items-center gap-1.5 text-xs">
        <Link to="/things" className="hover:text-fg">
          事物
        </Link>
        <span>/</span>
        <span>{type?.displayName}</span>
      </nav>
      <Archive thing={thing.data} />
      {type?.resourceName === SESSIONS && (
        <SessionDialog
          sessionId={thingId}
          dateKey={search.date ?? dayRange(latestAt ?? now).key}
          view={search.view ?? "dialog"}
          onView={(view) =>
            void navigate({ search: (prev) => ({ ...prev, view: view === "dialog" ? undefined : view }) })
          }
        />
      )}
      <State thingId={thingId} />
      <History thingId={thingId} />
      <DeleteThing thing={thing.data} onDeleted={setDeleted} />
    </section>
  );
}

/** 现在的样子：关于它的状态。名字在档案中间，同一在“和它有关的”。 */
function State({ thingId }: { thingId: string }) {
  const archive = useArchive(thingId);
  const state = archive.data?.state ?? [];
  if (!state.length) return null;
  return (
    <Card className="mt-6">
      <SectionTitle>现在的样子</SectionTitle>
      <ul>
        {state.map((observation) => (
          <StatementRow key={observation.observationId} observation={observation} />
        ))}
      </ul>
    </Card>
  );
}

const HISTORY_STEP = 20;

/** 历史：关于它说过的全部话，包括被否定和被替换的，从这里撤回否定。 */
function History({ thingId }: { thingId: string }) {
  const history = useHistory(thingId);
  const [shown, setShown] = useState(HISTORY_STEP);
  const entries = history.data ?? [];
  if (!entries.length) return null;
  return (
    <Card className="mt-4">
      <SectionTitle>历史 · {entries.length}</SectionTitle>
      <ul>
        {entries.slice(0, shown).map(({ observation, status }) => (
          <StatementRow key={observation.observationId} observation={observation} status={status} />
        ))}
      </ul>
      {shown < entries.length && (
        <button
          type="button"
          onClick={() => setShown(shown + HISTORY_STEP)}
          className="text-fg-3 hover:text-fg mt-1 cursor-pointer text-xs"
        >
          还有 {entries.length - shown} 条
        </button>
      )}
    </Card>
  );
}

function DeleteThing({ thing, onDeleted }: { thing: Thing; onDeleted: (result: DeleteResult) => void }) {
  const source = useSource();
  const client = useQueryClient();
  const archive = useArchive(thing.thingId);
  const [open, setOpen] = useState(false);
  const remove = useMutation({
    mutationFn: () => source.deleteThing(thing.thingId),
    onSuccess: (result) => {
      setOpen(false);
      onDeleted(result);
      refreshView(client);
    },
  });
  const parts = (archive.data?.activity ?? []).filter((item) => item.role === "part");
  const count = parts.reduce((sum, item) => sum + item.count, 0);
  return (
    <div className="mt-4 flex justify-end">
      <Button danger onClick={() => setOpen(true)}>
        删除这个事物
      </Button>
      <DeleteDialog
        open={open}
        title="删除这个事物"
        busy={remove.isPending}
        error={remove.error?.message ?? null}
        onClose={() => setOpen(false)}
        onConfirm={() => remove.mutate()}
      >
        <p>
          关于它的陈述{count > 0 && `和它的 ${count} 份组成部分`}
          一起移除，不可恢复，重读来源也不会回来。提到它的记录保留，追溯到这里时显示不可读取。
        </p>
      </DeleteDialog>
    </div>
  );
}

/** 删除之后：写明移除了多少，列出依据被删除内容的陈述。 */
function Deleted({ result }: { result: DeleteResult }) {
  return (
    <section>
      <Message title="已删除" detail={`移除了 ${result.deleted.length} 项记录`} mascot>
        <Link to="/things" className="text-fg-2 hover:text-fg text-[13px] underline underline-offset-4">
          回到事物
        </Link>
      </Message>
      <Dependents ids={result.dependents} />
    </section>
  );
}

/** 依据被删除内容的陈述，见 HISTORY-6。 */
export function Dependents({ ids }: { ids: readonly string[] }) {
  if (!ids.length) return null;
  return (
    <Card className="mt-4">
      <SectionTitle>依据被删除内容的陈述 · {ids.length}</SectionTitle>
      <div className="flex flex-wrap gap-1">
        {ids.map((id) => (
          <Token key={id} observationId={id} size="sm" />
        ))}
      </div>
    </Card>
  );
}
