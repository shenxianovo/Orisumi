import { createFileRoute, Link } from "@tanstack/react-router";
import { Archive } from "@/components/archive/Archive";
import { SessionDialog } from "@/components/sessions/SessionDialog";
import { Message } from "@/components/ui";
import { useThing } from "@/hooks/queries";
import { useArchive } from "@/hooks/useArchive";
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

/** 一个事物的档案；助手会话在织图下面显示这一天的对话。 */
function ThingPage() {
  const { thingId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const thing = useThing(thingId);
  const types = useTypes();
  const archive = useArchive(thingId);
  const now = useNow(60_000);

  if (thing.isPending) return <Message title="正在读取这个事物" />;
  if (!thing.data)
    return (
      <Message title="读不到这个事物" detail={thingId} mascot>
        <Link to="/things" className="text-fg-2 hover:text-fg text-[13px] underline underline-offset-4">
          回到事物
        </Link>
      </Message>
    );

  const resourceName = types.data?.bySchemaId.get(thing.data.schemaId)?.content.resourceName;
  const latest = archive.data?.bundles.find((bundle) => bundle.resourceName === INTERACTIONS)?.statements[0];
  const latestAt = parseTime(latest?.endAt ?? latest?.startAt);
  return (
    <section>
      <nav aria-label="位置" className="text-fg-3 mb-5 flex items-center gap-1.5 text-xs">
        <Link to="/things" className="hover:text-fg">
          事物
        </Link>
        <span>/</span>
        <span>{types.data?.bySchemaId.get(thing.data.schemaId)?.content.displayName}</span>
      </nav>
      <Archive thing={thing.data} />
      {resourceName === SESSIONS && (
        <SessionDialog
          sessionId={thingId}
          dateKey={search.date ?? dayRange(latestAt ?? now).key}
          view={search.view ?? "dialog"}
          onView={(view) =>
            void navigate({ search: (prev) => ({ ...prev, view: view === "dialog" ? undefined : view }) })
          }
        />
      )}
    </section>
  );
}
