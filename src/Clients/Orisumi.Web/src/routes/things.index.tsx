import { useMemo, useState } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { Search } from "lucide-react";
import { ApiError, readAllThings } from "@/api/client";
import { useSource } from "@/api/source";
import type { Thing } from "@/api/types";
import { ThingNode } from "@/components/archive/nodes";
import { DayNav } from "@/components/day/DayNav";
import { Message, PageHeader } from "@/components/ui";
import { useCatalog } from "@/hooks/useApplications";
import { useConversations } from "@/hooks/useConversations";
import { describeItem } from "@/hooks/useEntityInfo";
import { useNow } from "@/hooks/useNow";
import { useTypes } from "@/hooks/useTypes";
import { dayOf, dayRange } from "@/lib/time";

interface ThingsSearch {
  date?: string | undefined;
}

export const Route = createFileRoute("/things/")({
  validateSearch: (search: Record<string, unknown>): ThingsSearch =>
    typeof search.date === "string" && dayOf(search.date) ? { date: search.date } : {},
  component: ThingsPage,
});

function useThingList(resourceName: string) {
  const source = useSource();
  return useQuery({
    queryKey: ["things", { resourceName }, "all"],
    queryFn: () =>
      readAllThings((after) => source.listThings({ resourceName, limit: 1000, ...(after ? { after } : {}) })).catch(
        (error: unknown) => {
          // 资源名还没有登记时按没有数据处理。
          if (error instanceof ApiError && error.status === 404) return [];
          throw error;
        },
      ),
  });
}

/** 事物：记录提到的应用、设备、助手会话和人，点一个打开它的档案。 */
function ThingsPage() {
  const search = Route.useSearch();
  const now = useNow(60_000);
  const catalog = useCatalog();
  const conversations = useConversations(search.date ?? dayRange(now).key);
  const devices = useThingList("devices");
  const people = useThingList("people");
  const types = useTypes();
  const [query, setQuery] = useState("");

  const groups = useMemo(() => {
    const data = catalog.data;
    const applications = data
      ? [
          ...data.products.map((product) => product.thing),
          ...data.installs.filter((install) => !data.productOf(install.thing)).map((install) => install.thing),
        ]
      : [];
    const sessions = (conversations.groups ?? []).flatMap((group) => group.session ?? []);
    return [
      { title: "应用", things: applications },
      { title: "设备", things: devices.data ?? [] },
      { title: "助手会话", things: sessions, dated: true },
      { title: "人", things: people.data ?? [] },
    ];
  }, [catalog.data, conversations.groups, devices.data, people.data]);

  if (types.isPending || catalog.isPending) return <Message title="正在读取事物" />;
  const titled = (things: Thing[]) =>
    things
      .map((thing) => ({
        thing,
        title: describeItem(thing, types.data?.bySchemaId.get(thing.schemaId) ?? null).title,
      }))
      .filter(({ title }) => title.toLowerCase().includes(query.trim().toLowerCase()))
      .sort((a, b) => a.title.localeCompare(b.title, "zh"));

  return (
    <section>
      <PageHeader
        title="事物"
        summary={<p>记录提到的应用、设备、会话和人。点一个，看它的档案。</p>}
        actions={
          <label className="bg-card border-line focus-within:border-line-3 flex w-full items-center gap-1.5 rounded-lg border px-2.5 py-1.5 sm:w-64">
            <Search className="text-fg-3 size-3.5" aria-hidden="true" />
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="按名字筛选"
              aria-label="按名字筛选"
              className="placeholder:text-fg-3 min-w-0 flex-1 bg-transparent text-[13px] outline-none"
            />
          </label>
        }
      />
      <div className="grid gap-8">
        {groups.map(({ title, things, dated }) => {
          const list = titled(things);
          return (
            <section key={title} aria-label={title}>
              <div className="mb-2 flex flex-wrap items-center gap-x-3">
                <h2 className="text-[13px] font-medium">
                  {title} <span className="text-fg-3 text-xs font-normal tabular-nums">{list.length}</span>
                </h2>
                {dated && <DayNav start={conversations.range.start} today={conversations.today} small />}
              </div>
              {list.length ? (
                <div className="grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-2">
                  {list.map(({ thing }) => (
                    <ThingNode key={thing.entityId} thingId={thing.entityId} />
                  ))}
                </div>
              ) : (
                <p className="text-fg-3 text-[13px]">{query.trim() ? "没有符合的事物" : "这里还没有"}</p>
              )}
            </section>
          );
        })}
      </div>
    </section>
  );
}
